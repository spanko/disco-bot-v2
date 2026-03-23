# Discovery Chatbot v2

An enterprise-grade conversational discovery agent built on **Microsoft Foundry Agent Service (GA)** with the Responses API. Deploys as both a **web chat** and a **Microsoft Teams bot** from a single codebase. Designed for multi-tenant stamp-out deployment.

## Architecture

Built on the GA Foundry Responses API (replacing the deprecated Assistants/Threads API):
- **Agent definitions**: Versioned prompt agents managed by Foundry
- **Conversations**: Foundry-managed stateful conversations (BYO Cosmos DB)
- **Knowledge extraction**: Custom function tools + dual-write to Cosmos + AI Search
- **Data isolation**: One Foundry project per client, BYO resources per deployment

## Quick Start

### Prerequisites
- Azure CLI + logged in
- Azure Developer CLI (`azd`)
- .NET 9 SDK
- An Azure subscription with Azure AI Account Owner role

### Deploy a New Client Instance
```bash
# 1. Copy the parameter template
cp infra/params/template.bicepparam infra/params/my-client.bicepparam

# 2. Edit parameters (client name, region, model, deployer ID)
code infra/params/my-client.bicepparam

# 3. Deploy
azd up --environment my-client
```

## Project Structure

```
disco-bot-v2/
├── azure.yaml                  # azd project definition
├── config/
│   ├── instructions.md         # Agent system prompt
│   └── contexts/               # Pre-built discovery contexts
├── infra/                      # Stampable Bicep IaC
│   ├── main.bicep
│   ├── modules/                # Resource modules
│   └── params/                 # Per-client parameter files
├── src/
│   ├── DiscoveryAgent/         # Azure Functions app (API + runtime)
│   └── DiscoveryAgent.Core/    # Domain models (no Azure deps)
├── web/                        # Chat UI + Admin dashboard
├── tests/
└── scripts/
```

## SDK Packages
- `Azure.AI.Projects` (2.x) — Agent management, project client
- `Azure.AI.Projects.OpenAI` (2.x) — Responses API, conversations
- `Azure.Identity` — Authentication
- `Microsoft.Azure.Cosmos` — Knowledge store, contexts
- `Azure.Search.Documents` — Semantic search

## API Endpoints
| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/conversation` | Send a message, get agent response |
| GET | `/api/knowledge/{contextId}` | Browse extracted knowledge |
| GET | `/api/knowledge/{contextId}/search?q=` | Semantic search |
| GET | `/api/knowledge/{contextId}/summary` | Category breakdown |
| GET | `/api/health` | Health check |
