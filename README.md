# Azure AI Foundry Agent Memory — .NET Sample

A complete .NET 10 console application demonstrating long-term memory for Azure AI Foundry agents. The sample wraps the Foundry Memory Store REST API to show how to persist user preferences, context, and conversation summaries across sessions — without building your own embedding pipeline.

> **Full walkthrough:** [Beyond Stateless Conversations: Adding Long-Term Memory to Your Foundry Agents](https://codingwithramin.com/?p=592)

---

## What This Sample Demonstrates

- **Creating a Memory Store** — provisioning a named memory store with a chat model and embedding model, enabling both user-profile and chat-summary memory types
- **Memory Search Tool** — creating a Foundry agent with the `memory_search` tool attached so the platform handles retrieval automatically on every turn
- **Cross-session recall** — starting a new conversation and having the agent naturally use context from previous sessions without being explicitly told
- **User-scoped isolation** — deriving a per-user memory scope from the authenticated user's Entra ID (`tid_oid`), so each user's memories are completely isolated
- **Memory inspection and clearing** — `/memories` and `/clear` commands to inspect and reset stored memories during development

---

## How It Works

Memory in Foundry Agent Service operates in four phases:

1. **Extract** — the platform identifies key facts from each conversation turn (role, preferences, topics)
2. **Consolidate** — an LLM merges and deduplicates memories, resolving conflicts (e.g. user changes a preference)
3. **Retrieve** — at the start of each new conversation, relevant memories are surfaced via hybrid search; `user_profile` memories are injected immediately, `chat_summary` memories are retrieved per turn
4. **Customise** — the `user_profile_details` setting tells the system what kinds of information matter for your use case

### Memory Types

| Type | Description |
|---|---|
| `user_profile` | Persistent facts: role, preferences, restrictions — always retrieved |
| `chat_summary` | Condensed summaries of previous conversations — retrieved per turn |

---

## Prerequisites

### Azure

- Azure subscription with access to Microsoft Foundry
- A Foundry project — see [Step 1 of this post](https://codingwithramin.com/?p=493) if you need to create one
- **Azure AI User** RBAC role assigned to your identity

### Model Deployments

Deploy both of the following in your Foundry project's **Models + Endpoints** section:

| Purpose | Example deployment |
|---|---|
| Chat (extraction, consolidation, agent) | `gpt-4o` |
| Embeddings (memory retrieval) | `text-embedding-3-small` |

### Development

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2022 or VS Code
- Azure CLI or Visual Studio signed in — used by `DefaultAzureCredential`

---

## Project Structure

```
FoundryMemoryStoreDemo/
├── Program.cs               # Host setup, DI wiring, conversation loop
├── MemoryAgent.cs           # Agent creation, conversation management, scope resolution
├── MemoryStoreService.cs    # REST wrapper: create store, update/search/delete memories
├── appsettings.json         # Configuration template
└── appsettings.development.json  # Local overrides (not committed)
```

### Key Classes

**`MemoryStoreService`** — handles all Memory Store REST API calls:
- `CreateMemoryStoreAsync` — creates the store (idempotent; handles 409 Conflict and 400 "already exists")
- `SearchMemoriesAsync` — static retrieval (no query → user profile) or contextual retrieval (with query → semantic search)
- `UpdateMemoriesAsync` — submits conversation content for extraction, with debounce via `update_delay`
- `DeleteScopeAsync` — clears all memories for a given user scope

**`MemoryAgent`** — orchestrates the agent lifecycle:
- Resolves memory scope from the authenticated user's JWT (`tid_oid` format)
- Creates a per-user Foundry prompt agent with the `memory_search` tool
- Manages conversations via the Foundry Responses API
- Exposes `ShowStoredMemoriesAsync` / `ClearMemoriesAsync` for the console commands

---

## Configuration

### `appsettings.json`

```json
{
  "Foundry": {
    "ProjectEndpoint": "https://{your-ai-services-account}.services.ai.azure.com/api/projects/{project-name}",
    "ApiVersion": "2025-11-15-preview",
    "AgentApiVersion": "2025-11-15-preview",
    "TenantId": "YOUR_TENANT_ID"
  },
  "Models": {
    "ChatModel": "gpt-4o",
    "EmbeddingModel": "text-embedding-3-small"
  },
  "Memory": {
    "StoreName": "enterprise_memory_store",
    "StoreDescription": "Long-term memory for enterprise assistant",
    "UserProfileDetails": "Capture the user's role, department, document preferences, and frequently accessed topics. Avoid sensitive data such as financial details, credentials, and personal identifiers.",
    "UpdateDelaySeconds": 60
  }
}
```

| Setting | Notes |
|---|---|
| `Foundry:ProjectEndpoint` | Found in your Foundry project overview |
| `Foundry:TenantId` | Your Entra tenant ID — used by `DefaultAzureCredential` for multi-tenant disambiguation |
| `Models:ChatModel` | Must match a deployed chat model name in your project |
| `Models:EmbeddingModel` | Must match a deployed embedding model name in your project |
| `Memory:UserProfileDetails` | Tells the extractor what to focus on — be specific to reduce noise and cost |
| `Memory:UpdateDelaySeconds` | Debounce before writing memories. Use `0`–`1` for development, `300` for production |

Create an `appsettings.development.json` with your actual values (it is excluded from source control):

```json
{
  "Foundry": {
    "ProjectEndpoint": "https://my-account.services.ai.azure.com/api/projects/my-project",
    "TenantId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
  }
}
```

---

## Running the Sample

```bash
cd FoundryMemoryStoreDemo
dotnet run
```

On startup the application will:
1. Authenticate via `DefaultAzureCredential` (picks up Azure CLI, Visual Studio, or Managed Identity)
2. Derive your memory scope from the authenticated token (`tid_oid`)
3. Create the memory store if it doesn't already exist
4. Create (or reuse) a per-user Foundry agent with the `memory_search` tool
5. Start a conversation

### Console Commands

| Command | Action |
|---|---|
| `/memories` | Display all stored memories for the current user scope |
| `/clear` | Delete all memories for the current user scope |
| `/new` | Start a fresh conversation (stored memories persist across new conversations) |
| `/exit` | Quit the application |

---

## Testing the Memory Flow

**Session 1 — establish context:**

```
You: I'm a senior .NET developer at Contoso. I work mostly with SharePoint Online
     and Azure AI services. I prefer code examples in C#.
```

Type `/memories` to see what was captured:

```
Stored memories:
  [user_profile] Senior .NET developer at Contoso
  [user_profile] Works with SharePoint Online and Azure AI services
  [user_profile] Prefers code examples in C#
```

**Session 2 — test cross-session recall:**

Type `/new`, then ask a question without re-introducing yourself:

```
You: What approach would you recommend for searching HR documents in SharePoint?
```

The agent will use the context it stored from Session 1 — your role, tech stack, and language preference — without you repeating it.

> **Note on `UpdateDelaySeconds`:** Memories are written after the debounce period expires. Set this to `1` during development so you can verify extraction immediately. The stored memories shown by `/memories` reflect what has been committed at the time you call it.

---

## NuGet Packages

| Package | Version |
|---|---|
| `Azure.Identity` | 1.13.2 |
| `Microsoft.Extensions.Configuration` | 10.0.5 |
| `Microsoft.Extensions.Configuration.Json` | 10.0.5 |
| `Microsoft.Extensions.DependencyInjection` | 10.0.5 |
| `Microsoft.Extensions.Hosting` | 10.0.5 |
| `Microsoft.Extensions.Http` | 10.0.5 |
| `Microsoft.Extensions.Logging` | 10.0.5 |
| `Microsoft.Extensions.Logging.Console` | 10.0.5 |

No Azure AI SDK package is required. All Foundry API calls are made directly via `HttpClient` using `DefaultAzureCredential` for authentication. This approach is intentional — the .NET SDK (`Azure.AI.Projects`) does not yet have native memory support as of this writing (March 2026). When SDK support ships, the `MemoryStoreService` class can be swapped out without changing any other application code.

---

## Limitations (Public Preview)

- **10,000 memory items** per scope maximum
- **1,000 requests per minute** across all memory operations
- **No granular deletion** — you can clear an entire scope but cannot delete individual memory items via the API
- **Consolidation is non-deterministic** — the LLM-based merge occasionally stores duplicate or conflicting facts; this may improve as the preview matures
- **Write latency** — memories appear only after the `update_delay` period elapses; set it low during development

---

## Related Posts

This sample is part of a series on building production-grade Foundry agents in .NET:

- [Building Bing Search Agents with Azure AI Foundry](https://codingwithramin.com/?p=493)
- [SharePoint Grounding with Foundry Agent Service](https://codingwithramin.com/?p=518)
- [Delegated Permissions for Foundry Agents](https://codingwithramin.com/?p=558)
- **[Beyond Stateless Conversations: Adding Long-Term Memory to Your Foundry Agents](https://codingwithramin.com/?p=592)** ← this sample

---

## Resources

- [Memory in Foundry Agent Service — Concepts](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/what-is-memory)
- [Create and Use Memory — How-To Guide](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/memory-usage)
- [Python Code Samples](https://github.com/Azure/azure-sdk-for-python/tree/main/sdk/ai/azure-ai-projects/samples/memories)
- [Foundry Agent Service Overview](https://learn.microsoft.com/en-us/azure/foundry/agents/overview)

---

## License

MIT — see [LICENSE](LICENSE).
