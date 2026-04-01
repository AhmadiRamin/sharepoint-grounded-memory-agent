using FoundrySharePointMemoryAgent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

var configuration = new ConfigurationBuilder()
	.SetBasePath(Directory.GetCurrentDirectory())
	.AddJsonFile("appsettings.development.json", optional: false, reloadOnChange: true)
	.AddEnvironmentVariables()
	.Build();

builder.Services.AddSingleton<IConfiguration>(configuration);
builder.Services.AddHttpClient<MemoryStoreService>();
builder.Services.AddHttpClient<SharePointMemoryAgent>();
builder.Services.AddSingleton<MemoryStoreService>();
builder.Services.AddSingleton<SharePointMemoryAgent>();

builder.Services.AddLogging(logging =>
{
	logging.AddConsole();
});

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();
var agent = host.Services.GetRequiredService<SharePointMemoryAgent>();

try
{
	logger.LogInformation("Initializing Memory Agent...");
	await agent.InitializeAsync();

	Console.WriteLine("\n=== Foundry Agent with Long-Term Memory ===");
	Console.WriteLine("Commands:");
	Console.WriteLine("  /memories  - Show stored memories for this user");
	Console.WriteLine("  /clear     - Clear all memories for this user");
	Console.WriteLine("  /new       - Start a new conversation (memories persist)");
	Console.WriteLine("  /exit      - Exit the application\n");

	await agent.StartNewConversationAsync();

	while (true)
	{
		Console.Write("\nYou: ");
		var input = Console.ReadLine();

		if (string.IsNullOrEmpty(input))
			continue;

		switch (input.ToLower().Trim())
		{
			case "/exit":
				goto exit;

			case "/memories":
				Console.WriteLine("\nStored memories:");
				await agent.ShowStoredMemoriesAsync();
				continue;

			case "/clear":
				await agent.ClearMemoriesAsync();
				Console.WriteLine("Memories cleared.");
				continue;

			case "/new":
				await agent.StartNewConversationAsync();
				Console.WriteLine("New conversation started. Memories from previous sessions are still available.");
				continue;
		}

		var response = await agent.SendMessageAsync(input);
		Console.WriteLine($"\nAssistant: {response}");
	}
}
catch (Exception ex)
{
	logger.LogError(ex, "An error occurred: {Message}", ex.Message);
}

exit:
logger.LogInformation("Application completed");
await host.StopAsync();await host.StopAsync();