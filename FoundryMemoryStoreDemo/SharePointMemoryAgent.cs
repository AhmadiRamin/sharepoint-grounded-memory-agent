using Azure.Identity;
using FoundrySharePointMemoryAgent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FoundrySharePointMemoryAgent;

public class SharePointMemoryAgent
{
	private readonly MemoryStoreService _memoryService;
	private readonly IConfiguration _configuration;
	private readonly ILogger<SharePointMemoryAgent> _logger;
	private readonly HttpClient _httpClient;
	private readonly DefaultAzureCredential _credential;

	private readonly string _endpoint;
	private readonly string _storeName;
	private readonly string _agentApiVersion;

	private string? _agentName;
	private string? _conversationId;
	private string _scope = "dev_user_001";
	private string? _lastUpdateId;

	public SharePointMemoryAgent(
		MemoryStoreService memoryService,
		HttpClient httpClient,
		IConfiguration configuration,
		ILogger<SharePointMemoryAgent> logger)
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
		var parts = jwt.Split('.');
		if (parts.Length < 2) return "dev_user_001";

		var payload = parts[1];
		payload = payload.Replace('-', '+').Replace('_', '/');
		payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

		var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
		var doc = JsonDocument.Parse(json);

		var tid = doc.RootElement.TryGetProperty("tid", out var tidProp) ? tidProp.GetString() : null;
		var oid = doc.RootElement.TryGetProperty("oid", out var oidProp) ? oidProp.GetString() : null;

		return (!string.IsNullOrEmpty(tid) && !string.IsNullOrEmpty(oid))
			? $"{tid}_{oid}" : "dev_user_001";
	}

	private async Task<string?> ResolveSharePointConnectionIdAsync()
	{
		var connectionName = _configuration["SharePoint:ConnectionName"];
		if (string.IsNullOrEmpty(connectionName))
		{
			_logger.LogWarning("SharePoint connection name not configured");
			return null;
		}

		await SetAuthHeaderAsync();

		var response = await _httpClient.GetAsync(
			$"{_endpoint}/connections/{connectionName}?api-version={_agentApiVersion}");

		if (!response.IsSuccessStatusCode)
		{
			var error = await response.Content.ReadAsStringAsync();
			_logger.LogError("Failed to resolve SharePoint connection: {Error}", error);
			return null;
		}

		var result = await response.Content.ReadAsStringAsync();
		var doc = JsonDocument.Parse(result);

		if (doc.RootElement.TryGetProperty("id", out var idProp))
		{
			var connectionId = idProp.GetString();
			_logger.LogInformation("Resolved SharePoint connection: {Id}", connectionId);
			return connectionId;
		}

		return null;
	}

	private async Task<bool> TryGetExistingAgentAsync(string agentName)
	{
		var response = await _httpClient.GetAsync(
			$"{_endpoint}/agents/{agentName}?api-version={_agentApiVersion}");

		if (response.IsSuccessStatusCode)
		{
			_agentName = agentName;
			_logger.LogInformation("Agent '{Name}' already exists, reusing", _agentName);
			return true;
		}

		if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
			return false;

		var error = await response.Content.ReadAsStringAsync();
		_logger.LogWarning("Unexpected response checking agent existence: {Status} - {Error}",
			response.StatusCode, error);
		return false;
	}

	public async Task InitializeAsync()
	{
		var token = await SetAuthHeaderAsync();
		_scope = ResolveScopeFromToken(token);
		_logger.LogInformation("Using memory scope: {Scope}", _scope);

		var created = await _memoryService.CreateMemoryStoreAsync(_storeName);
		if (!created)
			throw new InvalidOperationException("Failed to create or verify memory store");

		var scopeHash = Math.Abs(_scope.GetHashCode()).ToString();
		var agentName = $"SharePointMemoryAgent";

		if (await TryGetExistingAgentAsync(agentName))
			return;

		var sharepointConnectionId = await ResolveSharePointConnectionIdAsync();

		await SetAuthHeaderAsync();

		var chatModel = _configuration["Models:ChatModel"] ?? "gpt-4o";
		var updateDelay = int.Parse(_configuration["Memory:UpdateDelaySeconds"] ?? "5");

		var tools = new List<object>
		{
			new
			{
				type = "memory_search_preview",
				memory_store_name = _storeName,
				scope = _scope,
				update_delay = updateDelay
			}
		};

		if (!string.IsNullOrEmpty(sharepointConnectionId))
		{
			tools.Add(new
			{
				type = "sharepoint_grounding_preview",
				sharepoint_grounding_preview = new
				{
					project_connections = new[]
					{
						new { project_connection_id = sharepointConnectionId }
					}
				}
			});
			_logger.LogInformation("SharePoint grounding tool added to agent");
		}
		else
		{
			_logger.LogWarning("SharePoint not available - running with memory only");
		}

		var agentPayload = new
		{
			name = agentName,
			definition = new
			{
				kind = "prompt",
				model = chatModel,
				instructions = @"You are a knowledgeable enterprise assistant with two capabilities:

1. Long-term memory: You remember information about each user across conversations --
   their role, department, preferences, and what they've asked about before.

2. SharePoint search: You can search enterprise documents stored in SharePoint to
   provide accurate, up-to-date answers grounded in official company content.

When responding:
- Use what you know about the user from memory to tailor your answers. If you know
  they work in legal, emphasise compliance aspects. If they prefer summaries, be concise.
- When you find relevant SharePoint documents, cite them naturally. Explain what you
  found and why it's relevant to this user's question.
- If a user asks about something from a previous session, use that context naturally.
  Don't announce that you're recalling from memory.
- If you're unsure whether stored context is still accurate, confirm with the user.
- If a user shares new information about themselves, acknowledge it naturally.",
				tools
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
			_logger.LogInformation("Agent '{Name}' initialized with memory + SharePoint", _agentName);
			return;
		}

		if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
		{
			_agentName = agentName;
			_logger.LogInformation("Agent '{Name}' already exists, reusing", _agentName);
			return;
		}

		var err = await response.Content.ReadAsStringAsync();
		throw new InvalidOperationException($"Failed to create agent: {err}");
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
			throw new InvalidOperationException(
				$"Failed to create conversation: {response.StatusCode} - {error}");
		}

		var result = await response.Content.ReadAsStringAsync();
		var doc = JsonDocument.Parse(result);
		_conversationId = doc.RootElement.GetProperty("id").GetString();
		_lastUpdateId = null;
		_logger.LogInformation("Started conversation: {Id}", _conversationId);

		// Static retrieval: scope only (no items) → returns user_profile memories
		var staticMemories = await _memoryService.SearchMemoriesAsync(_storeName, _scope);
		// Contextual retrieval: scope + items → returns both user_profile AND chat_summary
		var contextualMemories = await _memoryService.SearchMemoriesAsync(
			_storeName, _scope, query: "previous conversations and user information");
		var totalLoaded = staticMemories.Select(m => m.MemoryId)
			.Union(contextualMemories.Select(m => m.MemoryId)).Count();
		if (totalLoaded > 0)
			_logger.LogInformation("Loaded {Count} stored memories ({Static} user_profile, {Contextual} contextual) for this user",
				totalLoaded, staticMemories.Count, contextualMemories.Count);
	}

	public async Task<string> SendMessageAsync(string userMessage)
	{
		if (_conversationId == null || _agentName == null)
			throw new InvalidOperationException(
				"Call InitializeAsync and StartNewConversationAsync first");

		await SetAuthHeaderAsync();

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
			return "Sorry, I encountered an error processing your request.";
		}

		var result = await response.Content.ReadAsStringAsync();
		_logger.LogDebug("Raw agent response: {Response}", result);

		var doc = JsonDocument.Parse(result);

		var outputText = "";
		var citations = new List<string>();

		if (doc.RootElement.TryGetProperty("output", out var output))
		{
			foreach (var item in output.EnumerateArray())
			{
				var itemType = item.TryGetProperty("type", out var typeProp)
					? typeProp.GetString() : null;

				if (itemType == "message" && item.TryGetProperty("content", out var msgContent))
				{
					foreach (var part in msgContent.EnumerateArray())
					{
						if (part.TryGetProperty("text", out var text))
							outputText += text.GetString();

						ExtractAnnotationCitations(part, citations);
					}
				}

				// Tool result items may carry citation metadata
				if (itemType == "tool_result" || itemType == "web_search_call" ||
					itemType == "sharepoint_grounding_preview")
				{
					ExtractAnnotationCitations(item, citations);

					if (item.TryGetProperty("content", out var toolContent))
					{
						foreach (var part in toolContent.EnumerateArray())
							ExtractAnnotationCitations(part, citations);
					}
				}
			}
		}

		if (string.IsNullOrEmpty(outputText) &&
			doc.RootElement.TryGetProperty("output_text", out var fallback))
			outputText = fallback.GetString() ?? "";

		// Update memory with the conversation turn so chat_summary entries are generated
		if (!string.IsNullOrEmpty(outputText))
		{
			_lastUpdateId = await _memoryService.UpdateMemoriesAsync(
				_storeName, _scope, userMessage, outputText, _lastUpdateId);
		}

		// Deduplicate citations (same URL may appear multiple times)
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var uniqueCitations = citations.Where(c => seen.Add(c)).ToList();

		if (uniqueCitations.Count > 0)
		{
			outputText += "\n\nSources:";
			foreach (var c in uniqueCitations) outputText += $"\n{c}";
		}

		return outputText;
	}

	private void ExtractAnnotationCitations(JsonElement element, List<string> citations)
	{
		if (!element.TryGetProperty("annotations", out var annotations))
			return;

		foreach (var ann in annotations.EnumerateArray())
		{
			if (!ann.TryGetProperty("type", out var annType))
				continue;

			var type = annType.GetString();

			if (type == "url_citation" && ann.TryGetProperty("url", out var url))
			{
				var title = ann.TryGetProperty("title", out var t) && t.GetString() is { } tStr
					? tStr : url.GetString();
				citations.Add($"  {title} - {url.GetString()}");
			}
			else if (type == "file_citation" || type == "file_path")
			{
				var fileId = ann.TryGetProperty("file_id", out var fid) ? fid.GetString() : null;
				var filename = ann.TryGetProperty("filename", out var fn) ? fn.GetString() : fileId;
				if (!string.IsNullOrEmpty(filename))
					citations.Add($"  {filename}");
			}
		}
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

		foreach (var m in all)
			Console.WriteLine($"  [{m.MemoryType}] {m.Content}");
	}

	public async Task ClearMemoriesAsync()
	{
		await _memoryService.DeleteScopeAsync(_storeName, _scope);
	}
}