using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FoundrySharePointMemoryAgent;

public class MemoryStoreService
{
	private readonly HttpClient _httpClient;
	private readonly IConfiguration _configuration;
	private readonly ILogger<MemoryStoreService> _logger;
	private readonly DefaultAzureCredential _credential;
	private readonly string _endpoint;
	private readonly string _apiVersion;

	public MemoryStoreService(
		HttpClient httpClient,
		IConfiguration configuration,
		ILogger<MemoryStoreService> logger)
	{
		_httpClient = httpClient;
		_configuration = configuration;
		_logger = logger;

		_endpoint = _configuration["Foundry:ProjectEndpoint"]
			?? throw new InvalidOperationException("Foundry project endpoint is not configured");
		_apiVersion = _configuration["Foundry:ApiVersion"] ?? "2025-11-15-preview";

		var tenantId = _configuration["Foundry:TenantId"];
		_credential = string.IsNullOrEmpty(tenantId)
			? new DefaultAzureCredential()
			: new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = tenantId });
	}

	private async Task SetAuthHeaderAsync()
	{
		var token = await _credential.GetTokenAsync(
			new Azure.Core.TokenRequestContext(["https://ai.azure.com/.default"]));
		_httpClient.DefaultRequestHeaders.Authorization =
			new AuthenticationHeaderValue("Bearer", token.Token);
	}

	public async Task<bool> CreateMemoryStoreAsync(string storeName)
	{
		await SetAuthHeaderAsync();

		var chatModel = _configuration["Models:ChatModel"] ?? "gpt-4o";
		var embeddingModel = _configuration["Models:EmbeddingModel"] ?? "text-embedding-3-small";
		var description = _configuration["Memory:StoreDescription"] ?? "Agent memory store";
		var profileDetails = _configuration["Memory:UserProfileDetails"] ?? "";
		var summaryDetails = _configuration["Memory:ChatSummaryDetails"] ?? "";

		var payload = new
		{
			name = storeName,
			description,
			definition = new
			{
				kind = "default",
				chat_model = chatModel,
				embedding_model = embeddingModel,
				options = new
				{
					chat_summary_enabled = true,
					chat_summary_details = summaryDetails,
					user_profile_enabled = true,
					user_profile_details = profileDetails
				}
			}
		};

		var content = new StringContent(
			JsonSerializer.Serialize(payload, _jsonOptions),
			Encoding.UTF8, "application/json");

		var response = await _httpClient.PostAsync(
			$"{_endpoint}/memory_stores?api-version={_apiVersion}", content);

		if (response.IsSuccessStatusCode)
		{
			_logger.LogInformation("Memory store '{StoreName}' created successfully", storeName);
			return true;
		}

		// Store might already exist — check for conflict
		if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
		{
			_logger.LogInformation("Memory store '{StoreName}' already exists, patching options", storeName);
			await PatchMemoryStoreOptionsAsync(storeName, chatModel, embeddingModel, description, profileDetails, summaryDetails);
			return true;
		}

		var error = await response.Content.ReadAsStringAsync();

		// API returns 400 BadRequest when store already exists (instead of 409)
		if (response.StatusCode == System.Net.HttpStatusCode.BadRequest
			&& error.Contains("already exists", StringComparison.OrdinalIgnoreCase))
		{
			_logger.LogInformation("Memory store '{StoreName}' already exists, patching options", storeName);
			await PatchMemoryStoreOptionsAsync(storeName, chatModel, embeddingModel, description, profileDetails, summaryDetails);
			return true;
		}

		_logger.LogError("Failed to create memory store: {StatusCode} - {Error}",
			response.StatusCode, error);
		return false;
	}

	private async Task PatchMemoryStoreOptionsAsync(
		string storeName, string chatModel, string embeddingModel, string description,
		string profileDetails, string summaryDetails)
	{
		var patchPayload = new
		{
			description,
			definition = new
			{
				kind = "default",
				chat_model = chatModel,
				embedding_model = embeddingModel,
				options = new
				{
					chat_summary_enabled = true,
					chat_summary_details = summaryDetails,
					user_profile_enabled = true,
					user_profile_details = profileDetails
				}
			}
		};

		var request = new HttpRequestMessage(
			HttpMethod.Post,
			$"{_endpoint}/memory_stores/{storeName}?api-version={_apiVersion}")
		{
			Content = new StringContent(
				JsonSerializer.Serialize(patchPayload, _jsonOptions),
				Encoding.UTF8, "application/json")
		};

		var response = await _httpClient.SendAsync(request);

		if (response.IsSuccessStatusCode)
			_logger.LogInformation("Memory store '{StoreName}' options updated successfully", storeName);
		else
		{
			var error = await response.Content.ReadAsStringAsync();
			_logger.LogWarning("Could not PATCH memory store options ({Status}): {Error}",
				(int)response.StatusCode, error);
		}
	}

	public async Task<string?> UpdateMemoriesAsync(
		string storeName, string scope, string userMessage, string assistantMessage, string? previousUpdateId = null)
	{
		await SetAuthHeaderAsync();

		var payload = new
		{
			scope,
			items = new object[]
			{
				new
				{
					type = "message",
					role = "user",
					content = new[]
					{
						new { type = "input_text", text = userMessage }
					}
				},
				new
				{
					type = "message",
					role = "assistant",
					content = new[]
					{
						new { type = "output_text", text = assistantMessage }
					}
				}
			},
			update_delay = 0,
			previous_update_id = previousUpdateId
		};

		var serialized = JsonSerializer.Serialize(payload, _jsonOptions);
		_logger.LogDebug("update_memories payload: {Payload}", serialized);

		var content = new StringContent(serialized, Encoding.UTF8, "application/json");

		var response = await _httpClient.PostAsync(
			$"{_endpoint}/memory_stores/{storeName}:update_memories?api-version={_apiVersion}",
			content);

		var result = await response.Content.ReadAsStringAsync();
		_logger.LogDebug("update_memories response ({Status}): {Body}", (int)response.StatusCode, result);

		if (!response.IsSuccessStatusCode)
		{
			_logger.LogError("Failed to update memories: {Status} - {Error}", response.StatusCode, result);
			return null;
		}

		var doc = JsonDocument.Parse(result);

		if (doc.RootElement.TryGetProperty("update_id", out var updateId))
		{
			_logger.LogInformation("Memory update queued with ID: {UpdateId}", updateId.GetString());
			return updateId.GetString();
		}

		_logger.LogWarning("update_memories succeeded but no update_id in response. Full response: {Body}", result);
		return null;
	}

	public async Task<List<MemoryItem>> SearchMemoriesAsync(
		string storeName, string scope, string? query = null, int maxMemories = 10)
	{
		await SetAuthHeaderAsync();

		object payload;

		if (string.IsNullOrEmpty(query))
		{
			// Static retrieval — gets user profile memories without a query
			payload = new
			{
				scope,
				options = new { max_memories = maxMemories }
			};
		}
		else
		{
			// Contextual retrieval — searches based on the query
			payload = new
			{
				scope,
				items = new[]
				{
					new
					{
						type = "message",
						role = "user",
						content = new[]
						{
							new { type = "input_text", text = query }
						}
					}
				},
				options = new { max_memories = maxMemories }
			};
		}

		var content = new StringContent(
			JsonSerializer.Serialize(payload, _jsonOptions),
			Encoding.UTF8, "application/json");

		var response = await _httpClient.PostAsync(
			$"{_endpoint}/memory_stores/{storeName}:search_memories?api-version={_apiVersion}",
			content);

		if (!response.IsSuccessStatusCode)
		{
			var error = await response.Content.ReadAsStringAsync();
			_logger.LogError("Failed to search memories: {Error}", error);
			return [];
		}

		var result = await response.Content.ReadAsStringAsync();
		var doc = JsonDocument.Parse(result);

		var memories = new List<MemoryItem>();

		if (doc.RootElement.TryGetProperty("memories", out var memoriesArray))
		{
			foreach (var memory in memoriesArray.EnumerateArray())
			{
				if (memory.TryGetProperty("memory_item", out var item))
				{
					memories.Add(new MemoryItem
					{
						MemoryId = item.GetProperty("memory_id").GetString() ?? "",
						Content = item.GetProperty("content").GetString() ?? "",
						MemoryType = item.TryGetProperty("kind", out var kind) && kind.GetString() is { } k
							? k : "unknown"
					});
				}
			}
		}

		_logger.LogInformation("Retrieved {Count} memories for scope '{Scope}'",
			memories.Count, scope);
		return memories;
	}

	public async Task<bool> DeleteScopeAsync(string storeName, string scope)
	{
		await SetAuthHeaderAsync();

		var payload = new { scope };
		var content = new StringContent(
			JsonSerializer.Serialize(payload, _jsonOptions),
			Encoding.UTF8, "application/json");

		var response = await _httpClient.PostAsync(
			$"{_endpoint}/memory_stores/{storeName}:delete_scope?api-version={_apiVersion}",
			content);

		if (response.IsSuccessStatusCode)
		{
			_logger.LogInformation("Deleted memories for scope '{Scope}'", scope);
			return true;
		}

		var error = await response.Content.ReadAsStringAsync();
		_logger.LogError("Failed to delete scope: {Error}", error);
		return false;
	}

	private static readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};
}

public class MemoryItem
{
	public string MemoryId { get; set; } = "";
	public string Content { get; set; } = "";
	public string MemoryType { get; set; } = "";
}