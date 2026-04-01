# disco-bot-v2: Claude Code Session Instructions

## Project Context

Discovery Bot v2 is an enterprise conversational knowledge-extraction agent built on Microsoft Foundry Agent Service (GA Responses API). The repo is at `spanko/disco-bot-v2`. It runs as an ASP.NET Core 9 minimal API hosted on Azure Container Apps, with a separate Management Plane ACA for fleet operations.

### Live Deployments

| Service | URL | Status |
|---------|-----|--------|
| Discovery Bot (dev) | `https://discodev-app-vjnr3y.redglacier-9cc1ba98.eastus2.azurecontainerapps.io` | Deployed |
| Management Plane | `https://disco-mgmt-app.happypond-a1b0638a.eastus2.azurecontainerapps.io` | Deployed (unauthenticated) |

### Architecture Summary

| Layer | Technology | Notes |
|-------|-----------|-------|
| Compute (Bot) | Azure Container Apps (Consumption) | Scale-to-zero, 0.5 vCPU / 1 GiB, HTTP scaling, health probes configured |
| Compute (Mgmt) | Azure Container Apps (Consumption) | Separate ACA for GT fleet operations |
| API Framework | ASP.NET Core 9 Minimal APIs | All routes in `Program.cs` for both services |
| AI Backend | Azure AI Foundry Agent Service | Responses API, conversation-bound client pattern |
| Database | Azure Cosmos DB (BYO) | `discovery` database (bot) + `management` database (mgmt plane) |
| Search | Azure AI Search | Semantic index for knowledge items (not used in lightweight mode) |
| Storage | Azure Blob Storage | Document uploads, exports (not used in lightweight mode) |
| Observability | OpenTelemetry → Azure Monitor | Custom metrics via `DiscoveryMetrics` |
| CI/CD | GitHub Actions → ACR build → ACA update | Single-job pipeline on `ubuntu-latest` |
| Auth (Bot) | Multi-mode: none / magic_link / invite_code / entra_external | Controlled by `AUTH_MODE` env var |
| Auth (Mgmt) | **Currently open — needs Easy Auth** | See "Open Items" |
| Web UI | Static files served from container (`wwwroot/`) | 3 HTML files: index, admin, dashboard |

### Key Files

| File | Purpose |
|------|---------|
| **Discovery Bot** | |
| `src/DiscoveryAgent/Program.cs` | DI registration, route definitions, conversation mode branching, auth middleware, startup agent init |
| `src/DiscoveryAgent/Services/AgentManager.cs` | Agent version management, tool definitions, instructions loading |
| `src/DiscoveryAgent/Handlers/ConversationHandler.cs` | Full conversation turn lifecycle (standard/full mode) |
| `src/DiscoveryAgent/Handlers/LightweightConversationHandler.cs` | Stateless conversation handler using `PreviousResponseId` chaining |
| `src/DiscoveryAgent/Configuration/DiscoveryBotSettings.cs` | All env var bindings including `CONVERSATION_MODE`, `AUTH_MODE`, validation |
| `src/DiscoveryAgent/Auth/` | Auth framework: `IAuthService` + implementations for each mode |
| `src/DiscoveryAgent/Services/Lightweight/` | Null-pattern service implementations for lightweight mode (no Cosmos/Search/Blob) |
| `src/DiscoveryAgent/Telemetry/DiscoveryMetrics.cs` | Custom OTel metrics |
| `Dockerfile` | Multi-stage build for the bot |
| **Management Plane** | |
| `src/ManagementPlane/Program.cs` | Fleet API routes, Cosmos stamps container, ARM client |
| `src/ManagementPlane/Services/StampManager.cs` | Stamp CRUD, ARM deployment orchestration, pause/resume |
| `src/ManagementPlane/Services/FleetMonitor.cs` | Background health polling (5-min interval), fleet health aggregation |
| `src/ManagementPlane/Models/Stamp.cs` | Stamp record, enums (`StampStatus`, `ConversationMode`, `AuthMode`), request/response types |
| `Dockerfile.management` | Multi-stage build for the management plane |
| **Infrastructure** | |
| `infra/main.bicep` | Full infra orchestrator: Cosmos, AI Search, Storage, App Insights, ACR, ACA, RBAC |
| `infra/management-plane.bicep` | Management plane ACA infrastructure |
| `infra/modules/container-app.bicep` | ACA definition with health probes, conversation mode + auth mode params |
| `infra/modules/role-assignments.bicep` | RBAC including Cosmos data-plane role (`Built-in Data Contributor`) |
| `.github/workflows/deploy.yaml` | `az acr build` + `az containerapp update` on push to `main` |
| `config/instructions.md` | System prompt for the agent |

---

## Resolved NuGet Package Versions

These are the ACTUAL resolved versions from `dotnet list package`. Do NOT guess type names from training data — always verify against these packages by running `dotnet build` after changes.

| Package | Specifier | Resolved | Notes |
|---------|-----------|----------|-------|
| `Azure.AI.Projects` | `2.0.0-beta.*` | `2.0.0-beta.2` | `AIProjectClient`, `.Agents` namespace |
| `Azure.AI.Extensions.OpenAI` | `2.0.0-beta.*` | `2.0.0-beta.1` | `ProjectResponsesClient`, `ProjectConversationCreationOptions` |
| `Azure.AI.OpenAI` | `2.*` | `2.1.0` | OpenAI protocol types |
| `Azure.Identity` | `1.*` | `1.19.0` | `DefaultAzureCredential` |
| `Microsoft.Azure.Cosmos` | `3.*` | `3.58.0` | BYO Cosmos client |
| `Azure.Search.Documents` | `11.*` | `11.7.0` | BYO AI Search |
| `Azure.Storage.Blobs` | `12.*` | `12.27.0` | BYO Blob Storage |
| `Azure.Monitor.OpenTelemetry.AspNetCore` | `1.*` | `1.4.0` | OTel + Azure Monitor |

Transitive dependencies of note: `Azure.AI.Projects.Agents 2.0.0-beta.1`, `Azure.Core 1.51.1`, `OpenAI 2.9.1`.

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

    var followUp = new CreateResponseOptions();
    foreach (var input in inputItems)
        followUp.InputItems.Add(input);

    var nextResponse = await responseClient.CreateResponseAsync(followUp, ct);
    currentResponse = nextResponse.Value;
}
```

---

## Conversation Modes

Controlled by `CONVERSATION_MODE` env var. The same container image handles all three modes — `Program.cs` branches DI registrations and handler selection based on this value.

| Mode | Cosmos | AI Search | Blob Storage | Handler | Cost |
|------|--------|-----------|-------------|---------|------|
| `lightweight` | Not used | Not used | Not used | `LightweightConversationHandler` (uses `PreviousResponseId` chaining) | ~$0 |
| `standard` | BYO Serverless | Free tier | BYO | `ConversationHandler` (conversation-bound client) | ~$8.50/mo |
| `full` | BYO Provisioned 3,000 RU/s | Basic+ | BYO | `ConversationHandler` (Foundry enterprise_memory) | ~$100/mo |

In lightweight mode, null-pattern service implementations are registered (`src/DiscoveryAgent/Services/Lightweight/`), so the handler code doesn't need conditional logic — it just gets no-op services injected.

---

## Auth Framework

Controlled by `AUTH_MODE` env var. Auth middleware (`src/DiscoveryAgent/Auth/AuthMiddleware.cs`) runs on all `/api/*` routes.

| Mode | Implementation | Required Env Vars |
|------|---------------|-------------------|
| `none` | `NoneAuthService` — passes everything through | — |
| `magic_link` | `MagicLinkAuthService` — email → signed JWT cookie | `JWT_SIGNING_KEY` |
| `invite_code` | `InviteCodeAuthService` — shared code per discovery session | — |
| `entra_external` | `EntraAuthService` — Entra External ID validation | `ENTRA_TENANT_ID`, `ENTRA_CLIENT_ID` |

---

## Environment Variables

All configuration is injected as container environment variables. See `DiscoveryBotSettings.FromEnvironment()` for the full mapping. `settings.Validate()` is called at startup and will crash with a clear message listing all missing required vars.

### Discovery Bot

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `PROJECT_ENDPOINT` | Always | — | Foundry project endpoint |
| `MODEL_DEPLOYMENT_NAME` | No | `gpt-4o` | Model deployment name |
| `AGENT_NAME` | No | `discovery-agent` | Agent name |
| `CONVERSATION_MODE` | No | `standard` | `lightweight` / `standard` / `full` |
| `AUTH_MODE` | No | `none` | `none` / `magic_link` / `invite_code` / `entra_external` |
| `COSMOS_ENDPOINT` | standard/full | — | BYO Cosmos endpoint |
| `COSMOS_DATABASE` | No | `discovery` | Cosmos database name |
| `STORAGE_ENDPOINT` | standard/full | — | BYO Blob Storage endpoint |
| `AI_SEARCH_ENDPOINT` | standard/full | — | BYO AI Search endpoint |
| `KNOWLEDGE_INDEX_NAME` | No | `knowledge-items` | AI Search index name |
| `JWT_SIGNING_KEY` | magic_link auth | — | HMAC key for JWT tokens |
| `ENTRA_TENANT_ID` | entra_external auth | — | Entra tenant ID |
| `ENTRA_CLIENT_ID` | entra_external auth | — | Entra app registration client ID |
| `MAGIC_LINK_EXPIRY_HOURS` | No | `24` | Magic link token TTL |
| `INSTRUCTIONS_PATH` | No | `config/instructions.md` | System prompt file path |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | No | — | Enables OTel |

### Management Plane

| Variable | Required | Description |
|----------|----------|-------------|
| `COSMOS_ENDPOINT` | Yes | Cosmos endpoint (management database) |
| `COSMOS_DATABASE` | No (default: `management`) | Database name for stamp registry |
| `AZURE_SUBSCRIPTION_ID` | Yes | Subscription for ARM deployments |

---

## Management Plane

Separate ACA (`src/ManagementPlane/`) that manages the fleet of Discovery Bot stamps.

### API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/health` | Liveness check |
| `GET` | `/api/fleet/stamps` | List all stamps |
| `GET` | `/api/fleet/stamps/{stampId}` | Get single stamp |
| `POST` | `/api/fleet/stamps` | Provision new stamp (creates RG + ARM deployment) |
| `POST` | `/api/fleet/stamps/{stampId}/pause` | Pause stamp (scale to 0) |
| `POST` | `/api/fleet/stamps/{stampId}/resume` | Resume paused stamp |
| `GET` | `/api/fleet/health` | Aggregated fleet health |

### Stamp Model

A stamp represents a single client deployment. Key fields: `stampId`, `name`, `prefix`, `suffix`, `resourceGroup`, `conversationMode`, `authMode`, `status` (Provisioning/Active/Paused/Failed/Deprovisioning), `containerAppFqdn`, `lastHealthCheck`, `healthStatus`.

### FleetMonitor

Background service (`IHostedService`) that polls `/health/ready` on all Active/Paused stamps every 5 minutes. Updates `lastHealthCheck` and `healthStatus` in Cosmos. Skips stamps with no `containerAppFqdn` set.

---

## Infrastructure (Bicep)

| Module | Resource | Notes |
|--------|----------|-------|
| `container-app.bicep` | ACA Environment + App | Health probes, `conversationMode` + `authMode` params |
| `cosmos-db.bicep` | Cosmos DB + `discovery` database | 4 containers |
| `ai-search.bicep` | AI Search | `free` SKU for dev |
| `storage.bicep` | Blob Storage | 4 containers |
| `app-insights.bicep` | App Insights + Log Analytics | OTel sink |
| `role-assignments.bicep` | RBAC | Cosmos Operator + **Data Contributor**, Storage Blob, Search Index |
| `observability.bicep` | Alert rules (optional) | Gated by `enableObservability` |
| ACR (inline) | Container Registry (Basic) | ACA gets AcrPull role |
| `management-plane.bicep` | Management plane ACA | Separate infra |

### CI/CD

`.github/workflows/deploy.yaml` — single job on push to `main`:
1. Azure Login (workload identity federation)
2. `az acr build` — pushes `discovery-bot:{sha}` + `:latest`
3. `az containerapp update` — deploys new image

---

## Open Items

### 1. Stamp provisioning — Bicep URL won't work with ARM

**File**: `src/ManagementPlane/Services/StampManager.cs` (line 108)

`ProvisionStampAsync` sets `TemplateLink.Uri` to a raw GitHub URL pointing at `main.bicep`. ARM requires compiled JSON templates, not Bicep source. This will fail at deployment time.

**Options**:
- **Template Specs**: Publish `main.bicep` as an Azure Template Spec, reference it by resource ID. Most operationally clean — versioned, auditable, no external hosting.
- **Compiled ARM JSON**: Add a CI step to `az bicep build --file infra/main.bicep --outfile infra/main.json`, commit or host the JSON, reference that URL.
- **Inline template**: Load the compiled JSON as `Template` (BinaryData) instead of `TemplateLink`. Works but bulky.

### 2. No PATCH endpoint for stamps

**File**: `src/ManagementPlane/Program.cs`

There's no way to update an existing stamp record (e.g., set `ContainerAppFqdn` after deployment completes, change `Status` to `Active`). The existing dev stamp can't be registered without this.

**Fix**: Add `PATCH /api/fleet/stamps/{stampId}` that accepts a partial update body and merges with the existing record.

### 3. Easy Auth on Management Plane

**File**: `infra/management-plane.bicep`

The management plane is currently open to the internet. It needs an Entra app registration + Easy Auth configuration to restrict access to `us.gt.com` GT operators.

**Fix**: Create an Entra app registration (can be Bicep or manual), then configure Easy Auth on the ACA via `authConfigs` in the Bicep template. Define app roles `Platform.Operator` and `Platform.Viewer`.

### 4. FleetMonitor can't poll — no FQDNs set

**File**: `src/ManagementPlane/Services/FleetMonitor.cs` (line 53)

The background service skips stamps where `ContainerAppFqdn` is null. Since there's no PATCH endpoint (item #2), no stamps have FQDNs set, so health polling does nothing.

**Fix**: Depends on item #2. Once PATCH exists, register the dev stamp with its FQDN: `discodev-app-vjnr3y.redglacier-9cc1ba98.eastus2.azurecontainerapps.io`.

---

## Debugging Tips

- If `CreateAgentVersionAsync` returns 404: `PROJECT_ENDPOINT` is wrong. Format: `https://<resource>.services.ai.azure.com/api/projects/<project>`.
- If `CreateAgentVersionAsync` returns 403: managed identity needs `Azure AI User` role on the Foundry project.
- If "model must match the agent's model": you're passing a string to the `CreateResponseOptions` constructor. Use the default constructor; bind conversation at the client level.
- If tool calls never fire: check Foundry portal to verify the agent version has three function tools.
- If conversation creation fails with 400: BYO Cosmos capability host may not be configured in Foundry portal.
- If Cosmos data operations fail with 403: check that `Cosmos DB Built-in Data Contributor` role is assigned (data-plane, not just `Operator`).
- Cold starts: set `minReplicas: 1` in `container-app.bicep` for prod stamps.
- Stamp provisioning fails: ARM needs compiled JSON, not raw Bicep. See Open Item #1.

## Local Development

```bash
# Discovery Bot
cd src/DiscoveryAgent
dotnet user-secrets set "PROJECT_ENDPOINT" "https://..."
dotnet user-secrets set "CONVERSATION_MODE" "lightweight"  # avoids needing Cosmos/Search/Blob
dotnet run

# Management Plane
cd src/ManagementPlane
dotnet user-secrets set "COSMOS_ENDPOINT" "https://..."
dotnet user-secrets set "AZURE_SUBSCRIPTION_ID" "..."
dotnet run

# Docker (bot)
docker build -t discovery-bot .
docker run -p 8080:8080 -e PROJECT_ENDPOINT="https://..." -e CONVERSATION_MODE="lightweight" discovery-bot

# Docker (mgmt)
docker build -f Dockerfile.management -t disco-mgmt .
docker run -p 8081:8080 -e COSMOS_ENDPOINT="https://..." -e AZURE_SUBSCRIPTION_ID="..." disco-mgmt
```

**Note**: Docker builds target `linux/amd64`. On ARM machines, use `az acr build` or GitHub Actions.

## Testing

```bash
# Bot health
curl https://discodev-app-vjnr3y.redglacier-9cc1ba98.eastus2.azurecontainerapps.io/health

# Bot conversation
curl -X POST https://discodev-app-vjnr3y.redglacier-9cc1ba98.eastus2.azurecontainerapps.io/api/conversation \
  -H "Content-Type: application/json" \
  -d '{"userId": "test-user", "message": "Hi, I am a project manager."}'

# Management plane health
curl https://disco-mgmt-app.happypond-a1b0638a.eastus2.azurecontainerapps.io/health

# List stamps
curl https://disco-mgmt-app.happypond-a1b0638a.eastus2.azurecontainerapps.io/api/fleet/stamps

# Fleet health
curl https://disco-mgmt-app.happypond-a1b0638a.eastus2.azurecontainerapps.io/api/fleet/health
```
