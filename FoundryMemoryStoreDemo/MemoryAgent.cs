using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FoundrySharePointMemoryAgent;


public class MemoryAgent
{
	private readonly MemoryStoreService _memoryService;
	private readonly IConfiguration _configuration;
	private readonly ILogger<MemoryAgent> _logger;
	private readonly HttpClient _httpClient;
	private readonly DefaultAzureCredential _credential;

	private readonly string _endpoint;
	private readonly string _storeName;
	private readonly string _agentApiVersion;

	private string? _agentName;
	private string? _conversationId;
	private string? _lastUpdateId;
	private string _scope = "dev_user_001";

	public MemoryAgent(
		MemoryStoreService memoryService,
		HttpClient httpClient,
		IConfiguration configuration,
		ILogger<MemoryAgent> logger)
	{
		_memoryService = memoryService;
		_httpClient = httpClient;
		_configuration = configuration;
		_logger = logger;

		_endpoint = _configuration["Foundry:ProjectEndpoint"]
			?? throw new InvalidOperationException("Foundry project endpoint not configured");
		_storeName = _configuration["Memory:StoreName"] ?? "enterprise_memory_store";
		_agentApiVersion = _configuration["Foundry:AgentApiVersion"] ?? "2025-11-15-preview";

		var tenantId = _configuration["Foundry:TenantId"];
		_credential = string.IsNullOrEmpty(tenantId)
			? new DefaultAzureCredential()
			: new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = tenantId });
	}

	private async Task<string> SetAuthHeaderAsync()
	{
		var tokenResult = await _credential.GetTokenAsync(
			new Azure.Core.TokenRequestContext(["https://ai.azure.com/.default"]));
		_httpClient.DefaultRequestHeaders.Authorization =
			new AuthenticationHeaderValue("Bearer", tokenResult.Token);
		return tokenResult.Token;
	}

	private static string ResolveScopeFromToken(string jwt)
	{
		// JWT is three base64url parts separated by '.'
		// Decode the payload (second part) to extract tid and oid claims
		var parts = jwt.Split('.');
		if (parts.Length < 2)
			return "dev_user_001";

		var payload = parts[1];
		// Base64url → base64
		payload = payload.Replace('-', '+').Replace('_', '/');
		payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

		var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
		var doc = JsonDocument.Parse(json);

		var tid = doc.RootElement.TryGetProperty("tid", out var tidProp) ? tidProp.GetString() : null;
		var oid = doc.RootElement.TryGetProperty("oid", out var oidProp) ? oidProp.GetString() : null;

		if (!string.IsNullOrEmpty(tid) && !string.IsNullOrEmpty(oid))
			return $"{tid}_{oid}";

		return "dev_user_001";
	}

	public async Task InitializeAsync()
	{
		// Resolve the real user scope from the auth token (tid_oid format)
		var token = await SetAuthHeaderAsync();
		_scope = ResolveScopeFromToken(token);
		_logger.LogInformation("Using memory scope: {Scope}", _scope);

		// Create the memory store if it doesn't exist
		var created = await _memoryService.CreateMemoryStoreAsync(_storeName);
		if (!created)
			throw new InvalidOperationException("Failed to create or verify memory store");

		await SetAuthHeaderAsync();

		var chatModel = _configuration["Models:ChatModel"] ?? "gpt-4o";
		var updateDelay = int.Parse(_configuration["Memory:UpdateDelaySeconds"] ?? "5");

		// Use a per-user agent name so the scope embedded in the tool definition is correct
		// Name must be alphanumeric + hyphens only, start/end with alphanumeric, max 63 chars
		var scopeHash = Math.Abs(_scope.GetHashCode()).ToString();
		var agentName = $"MemoryAgent-{scopeHash[..Math.Min(8, scopeHash.Length)]}";

		var agentPayload = new
		{
			name = agentName,
			definition = new
			{
				kind = "prompt",
				model = chatModel,
				instructions = """
                    You are a helpful enterprise assistant. You have access to a memory system
                    that stores information about your users across conversations.

                    When a user shares information about themselves — their role, preferences,
                    projects they're working on, or anything else relevant — acknowledge it naturally.
                    You don't need to announce that you're "saving" it.

                    When you recall information from previous sessions, use it naturally in your
                    responses. Don't say "According to my memory" or "I recall from our previous
                    conversation." Just use the context as a knowledgeable assistant would.

                    If you're unsure whether stored context is still accurate, it's fine to
                    confirm with the user.
                    """,
				tools = new[]
				{
					new
					{
						type = "memory_search_preview",
						memory_store_name = _storeName,
						scope = _scope,
						update_delay = updateDelay
					}
				}
			}
		};

		var content = new StringContent(
			JsonSerializer.Serialize(agentPayload),
			Encoding.UTF8, "application/json");

		var response = await _httpClient.PostAsync(
			$"{_endpoint}/agents?api-version={_agentApiVersion}", content);

		if (response.IsSuccessStatusCode)
		{
			var result = await response.Content.ReadAsStringAsync();
			var doc = JsonDocument.Parse(result);
			_agentName = doc.RootElement.GetProperty("name").GetString();
			_logger.LogInformation("Agent '{AgentName}' initialized with memory", _agentName);
			return;
		}

		// Agent already exists — reuse it
		if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
		{
			_agentName = agentName;
			_logger.LogInformation("Agent '{AgentName}' already exists, reusing it", _agentName);
			return;
		}

		var error = await response.Content.ReadAsStringAsync();
		throw new InvalidOperationException($"Failed to create agent: {error}");
	}

	public async Task StartNewConversationAsync()
	{
		await SetAuthHeaderAsync();

		var response = await _httpClient.PostAsync(
			$"{_endpoint}/openai/v1/conversations",
			new StringContent("{}", Encoding.UTF8, "application/json"));

		if (!response.IsSuccessStatusCode)
		{
			var error = await response.Content.ReadAsStringAsync();
			throw new InvalidOperationException($"Failed to create conversation: {response.StatusCode} - {error}");
		}

		var result = await response.Content.ReadAsStringAsync();
		var doc = JsonDocument.Parse(result);
		_conversationId = doc.RootElement.GetProperty("id").GetString();

		_logger.LogInformation("Started conversation: {ConversationId}", _conversationId);

		// Static retrieval: user_profile memories to prime the conversation
		var staticMemories = await _memoryService.SearchMemoriesAsync(_storeName, _scope);
		// Contextual retrieval: chat_summary (and relevant user_profile) memories
		var contextualMemories = await _memoryService.SearchMemoriesAsync(
			_storeName, _scope, query: "previous conversations and user information");
		var totalLoaded = staticMemories.Select(m => m.MemoryId)
			.Union(contextualMemories.Select(m => m.MemoryId)).Count();
		if (totalLoaded > 0)
		{
			_logger.LogInformation("Loaded {Count} stored memories ({Static} user_profile, {Contextual} contextual) for this user",
				totalLoaded, staticMemories.Count, contextualMemories.Count);
		}
	}

	public async Task<string> SendMessageAsync(string userMessage)
	{
		if (_conversationId == null || _agentName == null)
			throw new InvalidOperationException("Call InitializeAsync and StartNewConversationAsync first");

		await SetAuthHeaderAsync();

		// The memory_search tool on the agent handles retrieval and update automatically per turn
		var payload = new
		{
			input = userMessage,
			conversation = _conversationId,
			agent_reference = new
			{
				type = "agent_reference",
				name = _agentName
			}
		};

		var content = new StringContent(
			JsonSerializer.Serialize(payload),
			Encoding.UTF8, "application/json");

		var response = await _httpClient.PostAsync(
			$"{_endpoint}/openai/v1/responses", content);

		if (!response.IsSuccessStatusCode)
		{
			var error = await response.Content.ReadAsStringAsync();
			_logger.LogError("Agent response failed: {Error}", error);
			return "I'm sorry, I encountered an error processing your request.";
		}

		var result = await response.Content.ReadAsStringAsync();
		var doc = JsonDocument.Parse(result);

		// Extract the text response from the output array
		var outputText = "";
		if (doc.RootElement.TryGetProperty("output", out var output))
		{
			foreach (var item in output.EnumerateArray())
			{
				if (item.TryGetProperty("type", out var type) &&
					type.GetString() == "message")
				{
					if (item.TryGetProperty("content", out var msgContent))
					{
						foreach (var part in msgContent.EnumerateArray())
						{
							if (part.TryGetProperty("text", out var text))
								outputText += text.GetString();
						}
					}
				}
			}
		}

		// Fall back to output_text if available
		if (string.IsNullOrEmpty(outputText) &&
			doc.RootElement.TryGetProperty("output_text", out var fallbackText))
		{
			outputText = fallbackText.GetString() ?? "";
		}

		return outputText;
	}

	public async Task ShowStoredMemoriesAsync()
	{
		// Static retrieval: scope only (no items) → returns user_profile memories
		var staticMemories = await _memoryService.SearchMemoriesAsync(_storeName, _scope);

		// Contextual retrieval: scope + items → returns both user_profile AND chat_summary
		var contextualMemories = await _memoryService.SearchMemoriesAsync(
			_storeName, _scope, query: "previous conversations and user information");

		// Merge, preferring contextual results and deduplicating by MemoryId
		var seen = new HashSet<string>();
		var all = new List<MemoryItem>();
		foreach (var m in contextualMemories.Concat(staticMemories))
		{
			if (seen.Add(m.MemoryId))
				all.Add(m);
		}

		if (all.Count == 0)
		{
			Console.WriteLine("  (No memories stored yet for this user)");
			return;
		}

		foreach (var memory in all)
		{
			Console.WriteLine($"  [{memory.MemoryType}] {memory.Content}");
		}
	}

	public async Task ClearMemoriesAsync()
	{
		await _memoryService.DeleteScopeAsync(_storeName, _scope);
	}
}