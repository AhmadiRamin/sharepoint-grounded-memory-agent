#pragma warning disable AAIP001
#pragma warning disable OPENAI001

using Azure;
using Azure.AI.Projects;
using Azure.AI.Projects.Memory;
using OpenAI.Responses;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FoundrySharePointMemoryAgent;

public class MemoryStoreService
{
	private readonly AIProjectMemoryStores _memStores;
	private readonly IConfiguration _configuration;
	private readonly ILogger<MemoryStoreService> _logger;

	public MemoryStoreService(
		AIProjectClient projectClient,
		IConfiguration configuration,
		ILogger<MemoryStoreService> logger)
	{
		_memStores = projectClient.GetAIProjectMemoryStoresClient();
		_configuration = configuration;
		_logger = logger;
	}

	public async Task<bool> CreateMemoryStoreAsync(string storeName)
	{
		// Check if store already exists; if so, nothing to do
		try
		{
			await _memStores.GetMemoryStoreAsync(storeName);
			_logger.LogInformation("Memory store '{StoreName}' already exists", storeName);
			return true;
		}
		catch (RequestFailedException ex) when (ex.Status == 404)
		{
			// Not found — create it below
		}

		var chatModel = _configuration["Models:ChatModel"] ?? "gpt-4o";
		var embeddingModel = _configuration["Models:EmbeddingModel"] ?? "text-embedding-3-small";
		var description = _configuration["Memory:StoreDescription"] ?? "Agent memory store";
		var profileDetails = _configuration["Memory:UserProfileDetails"] ?? "";

		var definition = new MemoryStoreDefaultDefinition(chatModel: chatModel, embeddingModel: embeddingModel);
		definition.Options = new MemoryStoreDefaultOptions(isUserProfileEnabled: true, isChatSummaryEnabled: true);
		if (!string.IsNullOrEmpty(profileDetails))
			definition.Options.UserProfileDetails = profileDetails;

		try
		{
			await _memStores.CreateMemoryStoreAsync(
				name: storeName,
				definition: definition,
				description: description);
			_logger.LogInformation("Memory store '{StoreName}' created successfully", storeName);
			return true;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to create memory store '{StoreName}'", storeName);
			return false;
		}
	}

	public async Task<string?> UpdateMemoriesAsync(
		string storeName, string scope, string userMessage, string assistantMessage, string? previousUpdateId = null)
	{
		var options = new MemoryUpdateOptions(scope) { UpdateDelay = 0 };
		if (!string.IsNullOrEmpty(previousUpdateId))
			options.PreviousUpdateId = previousUpdateId;

		options.Items.Add(ResponseItem.CreateUserMessageItem(userMessage));
		options.Items.Add(ResponseItem.CreateUserMessageItem($"Assistant: {assistantMessage}"));

		try
		{
			var result = await _memStores.WaitForMemoriesUpdateAsync(
				memoryStoreName: storeName,
				pollingInterval: 500,
				options: options);

			if (result.Status == MemoryStoreUpdateStatus.Failed)
			{
				_logger.LogError("Memory update failed: {Error}", result.ErrorDetails);
				return null;
			}

			_logger.LogInformation("Memory update queued with ID: {UpdateId}", result.UpdateId);
			return result.UpdateId;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to update memories");
			return null;
		}
	}

	public async Task<List<MemoryItem>> SearchMemoriesAsync(
		string storeName, string scope, string? query = null, int maxMemories = 10)
	{
		var options = new MemorySearchOptions(scope)
		{
			ResultOptions = new MemorySearchResultOptions { MaxMemories = maxMemories }
		};

		if (!string.IsNullOrEmpty(query))
			options.Items.Add(ResponseItem.CreateUserMessageItem(query));

		try
		{
			var response = await _memStores.SearchMemoriesAsync(
				memoryStoreName: storeName,
				options: options);

			var memories = response.Value.Memories
				.Select(m => new MemoryItem
				{
					MemoryId = m.MemoryItem.MemoryId ?? "",
					Content = m.MemoryItem.Content ?? "",
					MemoryType = ClassifyMemoryItem(m.MemoryItem)
				})
				.ToList();

			_logger.LogInformation("Retrieved {Count} memories for scope '{Scope}'", memories.Count, scope);
			return memories;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to search memories");
			return [];
		}
	}

	public async Task<bool> DeleteScopeAsync(string storeName, string scope)
	{
		try
		{
			await _memStores.DeleteScopeAsync(name: storeName, scope: scope);
			_logger.LogInformation("Deleted memories for scope '{Scope}'", scope);
			return true;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to delete scope '{Scope}'", scope);
			return false;
		}
	}

	// Use the concrete subtype rather than the inaccessible MemoryItemKind enum.
	private static string ClassifyMemoryItem(Azure.AI.Projects.Memory.MemoryItem item) => item switch
	{
		ChatSummaryMemoryItem => "chat_summary",
		UserProfileMemoryItem => "user_profile",
		_ => "unknown"
	};
}

public class MemoryItem
{
	public string MemoryId { get; set; } = "";
	public string Content { get; set; } = "";
	public string MemoryType { get; set; } = "";
}
