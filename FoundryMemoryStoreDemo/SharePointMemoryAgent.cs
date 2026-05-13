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

		var agentName = "SharePointMemoryAgent";

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
- When you do search SharePoint and find relevant documents, ALWAYS cite them.
  Include the document title and a direct link inline in your answer
  (e.g. 'According to [Policy Name](url), ...'). Never use SharePoint content
  without citing its source.
- ONLY search SharePoint when the user is asking a question about company content,
  policies, documents, or topics that would plausibly exist in enterprise documents.
  Do NOT search SharePoint when the user is sharing personal information about
  themselves (their role, preferences, department, working style, etc.). In those
  cases, simply acknowledge what they said and store it — no SharePoint search needed.
- When you do search SharePoint and find no relevant documents, do NOT mention the
  failed search at all. Just answer from memory or acknowledge the user's message.
- When a new session starts and you are given a summary of previous conversations,
  IMMEDIATELY search SharePoint for the latest versions of any documents or topics
  mentioned in that summary. Do this before the user asks — treat it as a required
  step when resuming from prior sessions.
- If a user asks about updates or new versions of something discussed before, ALWAYS
  search SharePoint first. Never say you haven't searched — search, then answer.
- If a user asks about something from a previous session, use that context naturally.
  Don't announce that you're recalling from memory.
- If you're unsure whether stored context is still accurate, confirm with the user.
- If a user shares new information about themselves, simply acknowledge it warmly and
  confirm you have noted it. Do not recap all their stored profile back to them.",
				tools
			}
		};

		var content = new StringContent(
			JsonSerializer.Serialize(agentPayload),
			Encoding.UTF8, "application/json");

		// Try to create; if it already exists, update it via POST to the named resource
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
			// Agent exists — update it so instruction changes are applied
			await SetAuthHeaderAsync();
			var patchResponse = await _httpClient.PostAsync(
				$"{_endpoint}/agents/{agentName}?api-version={_agentApiVersion}", content);

			_agentName = agentName;
			if (patchResponse.IsSuccessStatusCode)
				_logger.LogInformation("Agent '{Name}' updated with latest instructions", _agentName);
			else
				_logger.LogWarning("Agent '{Name}' exists but update failed; reusing as-is", _agentName);
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

		// If there are prior chat summaries, send a silent priming turn so the agent
		// searches SharePoint for updated documents before the user's first message.
		var summaries = contextualMemories
			.Concat(staticMemories)
			.Where(m => m.MemoryType == "chat_summary")
			.Select(m => m.Content)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (summaries.Count > 0)
		{
			var topicContext = string.Join("\n", summaries.Select((s, i) => $"- {s}"));
			var primingMessage =
				$"A new conversation session has started. Here is a summary of what we discussed previously:\n{topicContext}\n\n" +
				"Please search SharePoint now for the latest versions of any documents, policies, or topics " +
				"mentioned above. Retrieve and review the current content so you are ready to answer " +
				"questions about any updates. Do not reply to the user yet — this is an internal preparation step.";

			await SendMessageAsync(primingMessage, silent: true);
			_logger.LogInformation("Sent priming turn to agent with {Count} chat summaries", summaries.Count);
		}
	}

	public async Task<string> SendMessageAsync(string userMessage, bool silent = false)
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

		// Update memory with the conversation turn so chat_summary entries are generated.
		// Skip for silent (priming) turns — they are internal and should not be memorised.
		if (!silent && !string.IsNullOrEmpty(outputText))
		{
			_lastUpdateId = await _memoryService.UpdateMemoriesAsync(
				_storeName, _scope, userMessage, outputText, _lastUpdateId);
		}

		// Deduplicate citations (same URL may appear multiple times).
		// For silent priming turns, return empty string (caller discards the response).
		if (silent)
			return string.Empty;

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

		// Merge, preferring contextual results and deduplicating by MemoryId and by content
		var seenIds = new HashSet<string>();
		var seenContent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var all = new List<MemoryItem>();
		foreach (var m in contextualMemories.Concat(staticMemories))
		{
			if (seenIds.Add(m.MemoryId) && seenContent.Add(m.Content.Trim()))
				all.Add(m);
		}

		if (all.Count == 0)
		{
			Console.WriteLine("  (No memories stored yet for this user)");
			return;
		}

		foreach (var m in all.OrderBy(m => m.MemoryType == "chat_summary" ? 0 : 1))
			Console.WriteLine($"  [{m.MemoryType}] {m.Content}");
	}

	public async Task ClearMemoriesAsync()
	{
		await _memoryService.DeleteScopeAsync(_storeName, _scope);
	}
}