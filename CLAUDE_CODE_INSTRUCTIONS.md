# disco-bot-v2: Claude Code Session Instructions

## Project Context

Discovery Bot v2 is an enterprise conversational knowledge-extraction agent built on Microsoft Foundry Agent Service (GA Responses API). The repo is at `spanko/disco-bot-v2`. It runs as an ASP.NET Core 9 minimal API hosted on Azure Container Apps, using the Foundry Responses API with function tool calling for knowledge extraction, user profile capture, and questionnaire tracking.

### Architecture Summary

| Layer | Technology | Notes |
|-------|-----------|-------|
| Compute | Azure Container Apps (Consumption) | Scale-to-zero, 0.5 vCPU / 1 GiB, HTTP scaling rule |
| API Framework | ASP.NET Core 9 Minimal APIs | All routes in `Program.cs` |
| AI Backend | Azure AI Foundry Agent Service | Responses API, conversation-bound client pattern |
| Database | Azure Cosmos DB (BYO) | `discovery` database, 4 containers |
| Search | Azure AI Search | Semantic index for knowledge items |
| Storage | Azure Blob Storage | Document uploads, exports |
| Observability | OpenTelemetry → Azure Monitor | Custom metrics via `DiscoveryMetrics` |
| CI/CD | GitHub Actions → ACR build → ACA update | Single-job pipeline on `ubuntu-latest` |
| Auth | Currently unauthenticated (migration to Easy Auth planned) | See "Next Steps" |
| Web UI | Static files served from container (`wwwroot/`) | 3 HTML files: index, admin, dashboard |

### Key Files

| File | Purpose |
|------|---------|
| `src/DiscoveryAgent/Program.cs` | DI registration, route definitions, startup agent initialization |
| `src/DiscoveryAgent/Services/AgentManager.cs` | Agent version management, tool definitions, instructions loading |
| `src/DiscoveryAgent/Handlers/ConversationHandler.cs` | Full conversation turn lifecycle: create/resume conversation, response generation, tool call loop |
| `src/DiscoveryAgent/Configuration/DiscoveryBotSettings.cs` | Environment variable bindings (`PROJECT_ENDPOINT`, `COSMOS_ENDPOINT`, etc.) |
| `src/DiscoveryAgent/Telemetry/DiscoveryMetrics.cs` | Custom OTel metrics (conversations, tool calls, extraction confidence) |
| `Dockerfile` | Multi-stage build: SDK → publish → aspnet runtime, web UI copied to `wwwroot/`, config copied |
| `infra/main.bicep` | Full infrastructure orchestrator: Cosmos, AI Search, Storage, App Insights, ACR, ACA, RBAC |
| `infra/modules/container-app.bicep` | ACA environment + app definition with all env vars |
| `.github/workflows/deploy.yaml` | `az acr build` + `az containerapp update` on push to `main` |
| `config/instructions.md` | System prompt for the agent |

---

## Critical: NuGet Package Versions

These are the ACTUAL package references in the csproj. Do NOT guess type names from training data — always verify against these packages by running `dotnet build` after changes.

| Package | Version Specifier | Notes |
|---------|------------------|-------|
| `Azure.AI.Projects` | `2.0.0-beta.*` | Foundry GA SDK, includes `AIProjectClient`, `Agents` namespace |
| `Azure.AI.Extensions.OpenAI` | `2.0.0-beta.*` | `ProjectResponsesClient`, `ProjectConversationCreationOptions` |
| `Azure.AI.OpenAI` | `2.*` | OpenAI protocol types |
| `Azure.Identity` | `1.*` | `DefaultAzureCredential` |
| `Microsoft.Azure.Cosmos` | `3.*` | BYO Cosmos client |
| `Azure.Search.Documents` | `11.*` | BYO AI Search |
| `Azure.Storage.Blobs` | `12.*` | BYO Blob Storage |
| `Azure.Monitor.OpenTelemetry.AspNetCore` | `1.*` | OTel + Azure Monitor |

The `OPENAI001` warning is suppressed in the csproj — this is the standard way to acknowledge preview API usage.

---

## SDK Type Map (validated through build)

| Type | Namespace | Notes |
|------|-----------|-------|
| `AIProjectClient` | `Azure.AI.Projects` | Main entry point, constructed with `(Uri endpoint, TokenCredential)` |
| `PromptAgentDefinition` | `Azure.AI.Projects.Agents` | Agent definition — pass model deployment name to constructor |
| `AgentVersion` | `Azure.AI.Projects.Agents` | Returned wrapped in `ClientResult<AgentVersion>` — MUST use `.Value` to unwrap |
| `ProjectResponsesClient` | `Azure.AI.Extensions.OpenAI` | From `_projectClient.OpenAI.GetProjectResponsesClientForAgent(agentName, conversationId)` |
| `ProjectConversationCreationOptions` | `Azure.AI.Extensions.OpenAI` | For creating new conversations |
| `CreateResponseOptions` | `OpenAI.Responses` | **Default constructor only** — do NOT pass model or conversation ID to constructor |
| `ResponseResult` | `OpenAI.Responses` | The response type — use `.GetOutputText()` for text content |
| `FunctionCallResponseItem` | `OpenAI.Responses` | Check `item is FunctionCallResponseItem` in output loop |
| `ResponseTool.CreateFunctionTool(...)` | `OpenAI.Responses` | For building function tool definitions |
| `ResponseItem` | `OpenAI.Responses` | Static factory: `.CreateUserMessageItem()`, `.CreateSystemMessageItem()`, `.CreateFunctionCallOutputItem()` |

---

## Key API Patterns (CURRENT — validated in production code)

### Creating an agent version

```csharp
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using OpenAI.Responses;

var definition = new PromptAgentDefinition(settings.ModelDeploymentName)
{
    Instructions = instructions,
};
definition.Tools.Add(ResponseTool.CreateFunctionTool(
    functionName: "my_tool",
    functionDescription: "Does a thing",
    functionParameters: BinaryData.FromObjectAsJson(new { type = "object", properties = new { } }),
    strictModeEnabled: false));

var result = await _projectClient.Agents.CreateAgentVersionAsync(
    agentName: settings.AgentName,
    options: new(definition),
    cancellationToken: ct);

var agentVersion = result.Value; // MUST unwrap ClientResult<AgentVersion>
```

### Conversation-bound response client (THE CORRECT PATTERN)

```csharp
using Azure.AI.Extensions.OpenAI;

// Step 1: Create conversation (or reuse existing conversationId)
var conversation = await _projectClient.OpenAI.Conversations
    .CreateProjectConversationAsync(new ProjectConversationCreationOptions(), ct);
var conversationId = conversation.Value.Id;

// Step 2: Bind BOTH agent and conversation at client creation time
var responseClient = _projectClient.OpenAI.GetProjectResponsesClientForAgent(
    defaultAgent: agentName,
    defaultConversationId: conversationId);

// Step 3: Create response — NO model param, NO conversation ID in options
var options = new CreateResponseOptions();
options.InputItems.Add(ResponseItem.CreateUserMessageItem("Hello"));

var response = await responseClient.CreateResponseAsync(options, ct);
var result = response.Value;
Console.WriteLine(result.GetOutputText());
```

**CRITICAL**: The `CreateResponseOptions(string, IEnumerable<ResponseItem>)` constructor's first parameter is MODEL, not conversation ID. Passing a conversation ID there causes "model must match" errors. Always use the default constructor and bind the conversation at the client level.

### Function tool call loop

```csharp
var currentResponse = response.Value;

while (currentResponse.OutputItems.Any(item => item is FunctionCallResponseItem))
{
    var inputItems = new List<ResponseItem>();

    foreach (var item in currentResponse.OutputItems)
    {
        inputItems.Add(item);
        if (item is FunctionCallResponseItem functionCall)
        {
            var result = await ExecuteFunctionAsync(functionCall);
            inputItems.Add(ResponseItem.CreateFunctionCallOutputItem(
                functionCall.CallId, result));
        }
    }

    // Submit tool results on the SAME conversation-bound client.
    // Conversation binding provides continuity — no previousResponseId needed.
    var followUp = new CreateResponseOptions();
    foreach (var input in inputItems)
        followUp.InputItems.Add(input);

    var nextResponse = await responseClient.CreateResponseAsync(followUp, ct);
    currentResponse = nextResponse.Value;
}
```

---

## Environment Variables

All configuration is injected as container environment variables. See `DiscoveryBotSettings.FromEnvironment()` for the full mapping.

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `PROJECT_ENDPOINT` | Yes | — | Foundry project endpoint: `https://<resource>.services.ai.azure.com/api/projects/<project>` |
| `MODEL_DEPLOYMENT_NAME` | No | `gpt-4o` | Model deployment in your Foundry project |
| `AGENT_NAME` | No | `discovery-agent` | Agent name to register/reference |
| `COSMOS_ENDPOINT` | Yes | — | BYO Cosmos account endpoint |
| `COSMOS_DATABASE` | No | `discovery` | Cosmos database name |
| `STORAGE_ENDPOINT` | Yes | — | BYO Blob Storage endpoint |
| `AI_SEARCH_ENDPOINT` | Yes | — | BYO AI Search endpoint |
| `KNOWLEDGE_INDEX_NAME` | No | `knowledge-items` | AI Search index name |
| `INSTRUCTIONS_PATH` | No | `config/instructions.md` | Path to system prompt file |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | No | — | App Insights connection string (enables OTel) |

---

## Infrastructure (Bicep)

The infra is in `infra/main.bicep` with modules under `infra/modules/`. Key resources:

| Module | Resource | Notes |
|--------|----------|-------|
| `container-app.bicep` | ACA Environment + App | SystemAssigned MI, env vars from params, HTTP scaling 0→5 replicas |
| `cosmos-db.bicep` | Cosmos DB account + `discovery` database | 4 containers: `knowledge-items`, `discovery-sessions`, `questionnaires`, `user-profiles` |
| `ai-search.bicep` | AI Search | `free` SKU for dev |
| `storage.bicep` | Blob Storage | Containers: `uploads`, `questionnaires`, `exports`, `documents` |
| `app-insights.bicep` | App Insights + Log Analytics workspace | OTel sink |
| `role-assignments.bicep` | RBAC for ACA managed identity | Cosmos DB Operator, Storage Blob Contributor, Search Index Contributor |
| `observability.bicep` | Alert rules (optional) | Gated by `enableObservability` parameter |
| ACR (inline in main.bicep) | Container Registry (Basic) | ACA gets AcrPull role |

### Bicep Parameters

The `infra/params/dev.bicepparam` file has dev-specific values. `template.bicepparam` is the starting point for new stamps. Key params: `prefix`, `suffix`, `deployerObjectId`, `imageTag`.

Initial deploy uses `imageTag = ''` which deploys a placeholder `aspnetapp` image. After first `az acr build`, update with actual tag.

### CI/CD Pipeline

`.github/workflows/deploy.yaml` — single job on push to `main`:
1. Azure Login (workload identity federation — `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` secrets)
2. `az acr build` — builds Dockerfile, pushes `discovery-bot:{sha}` + `discovery-bot:latest` to ACR
3. `az containerapp update` — updates ACA to new image

---

## Known Issues & Gaps to Fix

### 1. RBAC: Cosmos DB role is insufficient for data operations

**File**: `infra/modules/role-assignments.bicep`

The app uses `Cosmos DB Operator` role (`230815da-...`), which is a management-plane role (create/delete databases). The app needs **data-plane** access to read/write documents.

**Fix**: Add `Cosmos DB Built-in Data Contributor` role (`00000000-0000-0000-0000-000000000002`). This is a Cosmos-native RBAC role, not an ARM role, so it requires a different assignment mechanism:

```bicep
// This is a Cosmos DB data-plane role — assigned at the Cosmos account scope, not resourceGroup
resource cosmosDataContributor 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmosAccount  // need a reference to the Cosmos account resource
  name: guid(cosmosAccountId, appPrincipalId, '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccountId}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: appPrincipalId
    scope: cosmosAccountId
  }
}
```

The current code works only because `DefaultAzureCredential` falls back to a key-based connection or the deployer's identity has broader access. In a clean stamp deploy, data operations will fail with 403.

### 2. Health probes not configured on ACA

**File**: `infra/modules/container-app.bicep`

The app exposes `/health` (liveness) and `/health/ready` (readiness) endpoints but the ACA definition doesn't configure probes. ACA will use TCP connect by default, which doesn't catch app-level failures.

**Fix**: Add to the container definition in `container-app.bicep`:

```bicep
probes: [
  {
    type: 'Liveness'
    httpGet: { path: '/health', port: 8080 }
    initialDelaySeconds: 10
    periodSeconds: 30
  }
  {
    type: 'Readiness'
    httpGet: { path: '/health/ready', port: 8080 }
    initialDelaySeconds: 15
    periodSeconds: 30
  }
]
```

### 3. `post-provision.sh` not wired into `azure.yaml`

**File**: `azure.yaml`

The `azure.yaml` doesn't reference `infra/scripts/post-provision.sh`. If using `azd provision`, the script won't run. Either wire it in:

```yaml
hooks:
  postprovision:
    shell: sh
    run: infra/scripts/post-provision.sh
```

Or delete the script since it's purely informational logging and the deploy workflow doesn't use `azd`.

### 4. Agent initialization blocks startup

**File**: `src/DiscoveryAgent/Program.cs` (lines 84-96)

`EnsureAgentExistsAsync()` runs before `app.Run()`. If Foundry is unreachable, the catch block logs the error but the app starts in a degraded state. The `/health/ready` endpoint doesn't check agent initialization status.

**Fix**: Either add an `_agentManager.IsInitialized` check to the readiness probe, or make initialization a background task with retry that the readiness probe gates on.

### 5. Startup crash risk: missing config

If required env vars (`PROJECT_ENDPOINT`, `COSMOS_ENDPOINT`, etc.) are empty, `CosmosClient("")` and `new Uri("")` will throw during DI registration, crashing the container before it can serve health checks.

**Fix**: Add validation in `DiscoveryBotSettings.FromEnvironment()` that throws a clear error message listing all missing required vars, or defer client construction to first use.

---

## Next Steps (Roadmap)

These are from the containerization plan — implement in order.

### Phase 1: Auth (Easy Auth + Multi-Audience)

Three deploy-time auth modes controlled by `AUTH_MODE` env var:

| `authMode` | Audience | Mechanism |
|------------|----------|-----------|
| `magic_link` | External stakeholders (no Azure AD) | Email → signed JWT cookie |
| `invite_code` | External stakeholders (simple) | Shared code per discovery session |
| `entra_external` | Client orgs with Azure AD | Entra External ID, Easy Auth on ACA |

GT operators always authenticate via `us.gt.com` Entra ID (Easy Auth). The management plane (Phase 3) will be a separate ACA with its own auth boundary.

### Phase 2: Conversation Modes

Three modes controlled by `CONVERSATION_MODE` env var:

| Mode | Cosmos | AI Search | PreviousResponseId Chaining | Cost |
|------|--------|-----------|----------------------------|------|
| `lightweight` | None | None | Yes (stateless) | ~$0 |
| `standard` | Serverless (BYO) | Free tier | No (conversation binding) | ~$8.50/mo |
| `full` | Provisioned 3,000 RU/s | Basic+ | No (Foundry enterprise_memory) | ~$100/mo |

Lightweight mode uses `PreviousResponseId` chaining instead of Foundry-managed conversations, eliminating the Cosmos dependency entirely. The same container image handles all three modes — behavior switches on the env var.

### Phase 3: GT Management Plane

A separate ACA (authenticated to `us.gt.com` only) that provisions/manages client stamps:

- Fleet dashboard (all stamps, health, usage)
- Stamp provisioning (ARM template deployment)
- Cost monitoring per stamp
- Idle stamp detection and auto-pause

### Phase 4: Azure Marketplace

Package the stamp as an ARM template for Marketplace distribution. Customer clicks Create, picks a conversation mode profile, and gets a running Discovery Bot.

---

## Debugging Tips

- If `CreateAgentVersionAsync` returns 404: the `PROJECT_ENDPOINT` is wrong or the project doesn't exist. Format must be `https://<resource>.services.ai.azure.com/api/projects/<project>`.
- If `CreateAgentVersionAsync` returns 403: the managed identity (or your `az login` identity) needs `Azure AI User` role on the Foundry project.
- If you get "model must match the agent's model": you're passing a conversation ID or other string as the model parameter to `CreateResponseOptions`. Use the default constructor and bind conversation at the client level.
- If tool calls never appear in the response: check the Foundry portal to verify the agent version shows the three function tools. If not, log `definition.Tools.Count` before the create call.
- If conversation creation fails with 400: the BYO Cosmos capability host may not be configured. Check Foundry portal → Project settings → Capability host.
- If knowledge items aren't written to Cosmos: check `KnowledgeStore` DI registration and that the `discovery` database + `knowledge-items` container exist.
- If Cosmos data operations fail with 403: the ACA managed identity has `Cosmos DB Operator` (management-plane) but needs `Cosmos DB Built-in Data Contributor` (data-plane). See Known Issue #1.
- Cold start issues: if ACA takes >30s to respond after scale-from-zero, set `minReplicas: 1` in `container-app.bicep` for prod stamps.

## Local Development

```bash
# Run locally (requires env vars or user secrets)
cd src/DiscoveryAgent
dotnet user-secrets set "PROJECT_ENDPOINT" "https://..."
dotnet user-secrets set "COSMOS_ENDPOINT" "https://..."
# ... set all required vars
dotnet run

# Or with Docker
docker build -t discovery-bot .
docker run -p 8080:8080 \
  -e PROJECT_ENDPOINT="https://..." \
  -e COSMOS_ENDPOINT="https://..." \
  discovery-bot
```

**Note**: Docker builds target `linux/amd64`. If you're on an ARM machine (e.g., Windows ARM laptop), build via `az acr build` or GitHub Actions instead of local `docker build`.

## Testing

```bash
# Health check
curl http://localhost:8080/health

# Readiness (checks Cosmos, AI Search, Storage connectivity)
curl http://localhost:8080/health/ready

# Start a conversation
curl -X POST http://localhost:8080/api/conversation \
  -H "Content-Type: application/json" \
  -d '{"userId": "test-user", "message": "Hi, I am a project manager."}'

# Continue a conversation (use conversationId from previous response)
curl -X POST http://localhost:8080/api/conversation \
  -H "Content-Type: application/json" \
  -d '{"userId": "test-user", "conversationId": "<id>", "message": "Our biggest challenge is legacy migration."}'

# With a discovery context
curl -X POST http://localhost:8080/api/conversation \
  -H "Content-Type: application/json" \
  -d '{"userId": "test-user", "contextId": "your-context-id", "message": "Let us get started."}'
```

Expected behavior on first message:
1. New Foundry conversation created (log: `Created conversation: ...`)
2. Agent responds with discovery questions about the user's role
3. `store_user_profile` tool fires (log: `Processing tool call: store_user_profile`)
4. `extract_knowledge` tool fires to capture facts from user's message
5. Response includes `extractedKnowledgeIds` in the JSON output
