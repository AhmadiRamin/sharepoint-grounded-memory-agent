# Foundry SharePoint Memory Agent

A .NET 10 console application demonstrating how to combine **Azure AI Foundry long-term memory** with **SharePoint grounding** to build an enterprise assistant that remembers users across sessions and searches your company's documents.

This project accompanies the blog series at [codingwithramin.com](https://codingwithramin.com).

## What It Does

- **Long-term memory**: The agent remembers each user's role, preferences, and conversation history across sessions using the Foundry Memory Store API. Memory persists between conversations without any custom embedding pipeline.
- **SharePoint grounding**: The agent searches enterprise documents in SharePoint and cites the sources it uses. Responses are grounded in your actual company content.
- **Combined experience**: The agent uses memory to personalise SharePoint searches — knowing a user is a compliance officer and prefers concise summaries shapes both what it searches and how it responds.

## Project Structure

| File | Purpose |
|---|---|
| `SharePointMemoryAgent.cs` | Primary agent: memory + SharePoint grounding combined |
| `MemoryAgent.cs` | Memory-only agent (no SharePoint), useful for scenarios that don't need document search |
| `MemoryStoreService.cs` | Handles all Foundry Memory Store API calls (create store, update memories, search memories, delete scope) |
| `Program.cs` | Entry point; wires up DI, runs `SharePointMemoryAgent` interactively |
| `appsettings.json` | Configuration schema with placeholder values |


## Prerequisites

- .NET 10 SDK
- Azure AI Foundry project with:
  - Chat model deployed (e.g., `gpt-4o`)
  - Embedding model deployed (e.g., `text-embedding-3-small`)
  - Memory store support enabled (`2025-11-15-preview` API or later)
- **For SharePoint grounding**:
  - Microsoft 365 Copilot license for each user, or pay-as-you-go enabled
  - SharePoint site with documents to search
  - SharePoint connection configured in your Foundry project
  - READ access to the target SharePoint site

## Configuration

Copy `appsettings.json` to `appsettings.development.json` and fill in your values:

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
    "StoreDescription": "Long-term memory for enterprise SharePoint assistant",
    "UserProfileDetails": "Capture the user's role, department, document preferences, frequently accessed topics, and SharePoint sites they commonly reference. Avoid sensitive data such as financial details, credentials, and personal identifiers.",
    "ChatSummaryDetails": "Summarize the topics, questions, and documents the user discussed or requested in this conversation. Include document names, policy sections referenced, and key questions asked.",
    "UpdateDelaySeconds": 10
  },
  "SharePoint": {
    "ConnectionName": "MainSharePointConnection"
  }
}
```

The `ConnectionName` is the name you gave the SharePoint connection in your Foundry project under **Connected Resources**. The app resolves the full connection ID at runtime via the Connections API.

## Authentication

The application uses `DefaultAzureCredential`. For local development:

```bash
az login --tenant YOUR_TENANT_ID
```

The signed-in user's `tid` and `oid` claims are extracted from the token to derive a unique memory scope per user. In production, this naturally isolates each user's memories.

### Required RBAC Roles

| Role | Resource | Why |
|---|---|---|
| Azure AI Developer (or Contributor) | Foundry project | Create/call agents, memory stores, conversations |
| Cognitive Services OpenAI User | Azure OpenAI / AI Services resource | Memory store uses the embedding model to vectorize text for contextual search |

> **Note**: The embedding model role is required for contextual memory retrieval (searching chat summaries). Without it, only static `user_profile` memories are returned.

## Running the Application

```bash
dotnet run
```

### Interactive Commands

| Command | Description |
|---|---|
| `/memories` | Show all memories stored for the current user (both `user_profile` and `chat_summary`) |
| `/new` | Start a new conversation — memories from previous sessions persist |
| `/clear` | Delete all memories for the current user |
| `/exit` | Exit the application |

## Memory Types

The Foundry Memory Store captures two types of memories automatically:

- **`user_profile`**: Facts about the user — role, department, preferences. Persists indefinitely.
- **`chat_summary`**: Summaries of what the user discussed in each session. Used to provide conversational continuity.

### How `/memories` Retrieves Both Types

The `/memories` command uses two separate API calls to surface both types:

1. **Static retrieval** (`search_memories` with scope only) — returns `user_profile` memories.
2. **Contextual retrieval** (`search_memories` with `items` containing recent messages) — returns `chat_summary` memories most relevant to the current conversation context.

Results from both calls are merged and deduplicated before display.

> This is important: calling `search_memories` with only a scope never returns `chat_summary` memories. The Foundry documentation states: *"To retrieve contextual memories, call search_memories with items set to the latest messages. This can return both user profile and chat summary memories most relevant to the given items."*

## Key Design Decisions

**`SharePointMemoryAgent` vs `MemoryAgent`**: Both classes exist so you can choose the right one. Use `MemoryAgent` when SharePoint access isn't needed. `SharePointMemoryAgent` adds the `sharepoint_grounding_preview` tool and citation extraction from response annotations.

**Graceful degradation**: If the SharePoint connection can't be resolved at startup, `SharePointMemoryAgent` continues running with memory only rather than failing.

**Agent reuse**: On startup, the agent checks whether a named agent already exists before creating one. If it does, it reuses it — avoiding orphaned agent resources on every run.

**Memory store patching**: If the memory store already exists, `CreateMemoryStoreAsync` patches its options (models, profile/summary instructions) rather than failing. This lets you update `ChatSummaryDetails` or `UserProfileDetails` in config and have them take effect without manually deleting the store.

**Scope derivation**: The memory scope is derived from the authenticated user's `tid_oid` token claims. Each user gets isolated memories automatically.

## Limitations

- **10,000 items per scope** in the memory store
- **1,000 RPM** throughput cap on memory operations
- **One SharePoint tool per agent** — point the connection at a higher-level site if you need multiple subsites
- **Same-tenant only** — SharePoint site and Foundry project must be in the same Azure AD tenant
- **No Teams publishing** — the SharePoint grounding tool doesn't work when the agent is published to Microsoft Teams

## Resources

- [Memory in Foundry Agent Service — Concepts](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/what-is-memory)
- [Create and Use Memory — How-To Guide](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/memory-usage)
- [SharePoint Tool Documentation](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/sharepoint)
- [Foundry Agent Service Overview](https://learn.microsoft.com/en-us/azure/foundry/agents/overview)
- [Blog: Building a SharePoint Agent That Remembers](https://codingwithramin.com)
