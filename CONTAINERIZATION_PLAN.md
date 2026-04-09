# Discovery Bot v2 — Containerization & Architecture Simplification

## Context
This document guides the migration from Azure Functions to a container-based architecture optimized for cost, operability, security, and Azure Marketplace distribution. Each component includes options ranked by those priorities.

## Current Architecture (What We're Migrating From)

| Component | Current | Monthly Cost (dev) | Pain Points |
|-----------|---------|-------------------|-------------|
| Compute | Azure Functions (Y1 Dynamic) | ~$0 (free tier) | 75s cold starts, DI flicker, ARM-incompatible local dev, zip deploy friction |
| Database | Cosmos DB (provisioned 400 RU/s) | ~$24/mo | Paying for idle capacity, can't scale to zero |
| Search | AI Search (Basic) | ~$75/mo | Fixed cost regardless of usage |
| Storage | Blob Storage (LRS) | ~$1/mo | Fine, no change needed |
| Web UI | Blob static website (3 HTML files) | ~$0 | Separate deployment, CORS config needed |
| Observability | App Insights | ~$0 (free tier) | Fine, no change needed |
| Auth | Function keys | $0 | No identity, no audit trail, shared secret |
| CI/CD | GitHub Actions → zip deploy + blob upload | $0 | Two separate deploy jobs, publish profile secret |

---

## Target Architecture

### Compute: Azure Container Apps (Consumption)

**Why ACA over Functions:**
- Scale to zero (same as Functions) but with min-replica=1 option to eliminate cold starts
- Same Docker image runs locally and in production
- Built-in Entra ID Easy Auth (no code changes)
- Single deploy target: push image to ACR, ACA pulls it
- ACA free tier: 180,000 vCPU-seconds + 360,000 GiB-seconds + 2M requests/month FREE per subscription
- For a discovery bot handling ~100 conversations/day, this stays well within free tier

**Configuration: 0.5 vCPU / 1 GiB, min-replicas=0 (dev) or min-replicas=1 (prod)**

### Migration Steps

#### Step 1: Convert from Azure Functions to ASP.NET Core Minimal API

The app currently uses the Azure Functions hosting model (`ConfigureFunctionsWebApplication`, `[Function]` attributes, `[HttpTrigger]`). Convert to ASP.NET Core minimal APIs:

**Replace Program.cs:**
```csharp
var builder = WebApplication.CreateBuilder(args);

// All existing DI registrations stay the same
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(_ => new AIProjectClient(...));
// ... etc

var app = builder.Build();

// Serve static files (web UI) from wwwroot/
app.UseStaticFiles();

// Health endpoints
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));
app.MapGet("/health/ready", async (HealthReadyService health) => await health.CheckAsync());

// Conversation API
app.MapPost("/api/conversation", async (ConversationRequest req, IConversationHandler handler, CancellationToken ct) =>
{
    var response = await handler.HandleAsync(req, ct);
    return Results.Ok(response);
});

// Knowledge APIs
app.MapGet("/api/knowledge/{contextId}", async (string contextId, int? skip, int? take, IKnowledgeQueryService qs) =>
    Results.Ok(await qs.GetByContextPaginatedAsync(contextId, skip ?? 0, take ?? 50)));
app.MapGet("/api/knowledge/{contextId}/search", async (string contextId, string q, IKnowledgeStore store) =>
    Results.Ok(await store.SearchAsync(q, contextId)));
app.MapGet("/api/knowledge/{contextId}/summary", async (string contextId, IKnowledgeQueryService qs) =>
    Results.Ok(await qs.GetCategorySummaryAsync(contextId)));
app.MapGet("/api/knowledge/{contextId}/provenance/{itemId}", async (string contextId, string itemId, IKnowledgeStore store) =>
{
    var p = await store.TraceOriginAsync(itemId, contextId);
    return p is null ? Results.NotFound() : Results.Ok(p);
});

// Admin APIs
app.MapGet("/api/manage/contexts", async (IContextManagementService ctx) => Results.Ok(await ctx.ListContextsAsync()));
app.MapPost("/api/manage/context", async (DiscoveryContext context, Database db) => { /* upsert logic */ });
app.MapGet("/api/manage/questionnaires", async (Database db) => { /* list logic */ });
app.MapPost("/api/manage/questionnaire", async (ParsedQuestionnaire q, Database db) => { /* upsert logic */ });

// Document upload
app.MapPost("/api/documents/upload", async (HttpRequest req, BlobServiceClient blobs) => { /* upload logic */ });
app.MapGet("/api/documents/{documentId}/content", async (string documentId, BlobServiceClient blobs) => { /* content logic */ });

app.Run();
```

**What changes:**
- Remove all `[Function]` and `[HttpTrigger]` attributes
- Remove `Microsoft.Azure.Functions.Worker*` NuGet packages
- Remove `host.json`
- Change `<OutputType>Exe</OutputType>` SDK to `Microsoft.NET.Sdk.Web`
- Add `app.UseStaticFiles()` and copy `web/` contents to `wwwroot/`
- Route definitions become `app.MapGet/MapPost` instead of separate Function classes

**What stays the same:**
- All DI registrations
- All service implementations (AgentManager, ConversationHandler, KnowledgeStore, etc.)
- All domain models
- Config via environment variables

#### Step 2: Create Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/DiscoveryAgent.Core/ DiscoveryAgent.Core/
COPY src/DiscoveryAgent/ DiscoveryAgent/
COPY config/ config/

RUN dotnet publish DiscoveryAgent/DiscoveryAgent.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Copy published app
COPY --from=build /app .

# Copy web UI into wwwroot for static file serving
COPY web/ wwwroot/

# Copy config (instructions.md)
COPY config/ config/

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "DiscoveryAgent.dll"]
```

#### Step 3: Create container-app.bicep

Replace `function-app.bicep` with:

```bicep
param containerAppName string
param containerAppEnvName string
param containerImage string
param location string
param tags object = {}

// App settings as params (injected from main.bicep outputs)
param projectEndpoint string
param modelDeploymentName string
param agentName string
param cosmosEndpoint string
param cosmosDatabase string
param storageEndpoint string
param aiSearchEndpoint string
param knowledgeIndexName string
param appInsightsConnectionString string

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerAppEnvName
  location: location
  tags: tags
  properties: {
    zoneRedundant: false
  }
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'discovery-bot'
          image: containerImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'PROJECT_ENDPOINT', value: projectEndpoint }
            { name: 'MODEL_DEPLOYMENT_NAME', value: modelDeploymentName }
            { name: 'AGENT_NAME', value: agentName }
            { name: 'COSMOS_ENDPOINT', value: cosmosEndpoint }
            { name: 'COSMOS_DATABASE', value: cosmosDatabase }
            { name: 'STORAGE_ENDPOINT', value: storageEndpoint }
            { name: 'AI_SEARCH_ENDPOINT', value: aiSearchEndpoint }
            { name: 'KNOWLEDGE_INDEX_NAME', value: knowledgeIndexName }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          ]
        }
      ]
      scale: {
        minReplicas: 0  // scale to zero for dev; set 1 for prod
        maxReplicas: 5
        rules: [
          {
            name: 'http-scaling'
            http: { metadata: { concurrentRequests: '10' } }
          }
        ]
      }
    }
  }
}

output containerAppName string = app.name
output containerAppFqdn string = app.properties.configuration.ingress.fqdn
output containerAppPrincipalId string = app.identity.principalId
```

#### Step 4: Create ACR and update CI/CD

Add ACR to main.bicep:
```bicep
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: replace('${baseName}acr${uniqueSuffix}', '-', '')
  location: location
  tags: tags
  sku: { name: 'Basic' }  // ~$5/mo, sufficient for dev
  properties: { adminUserEnabled: false }
}
```

Update GitHub Actions:
```yaml
name: Build and Deploy

on:
  push:
    branches: [main]

env:
  ACR_NAME: discdevacr3xr5ve
  IMAGE_NAME: discovery-bot
  ACA_NAME: discdev-app-3xr5ve
  RESOURCE_GROUP: discovery-dev

jobs:
  build-and-deploy:
    runs-on: ubuntu-latest
    permissions:
      id-token: write
      contents: read
    steps:
      - uses: actions/checkout@v4

      - uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - name: Build and push to ACR
        run: |
          az acr build --registry $ACR_NAME --image $IMAGE_NAME:${{ github.sha }} --image $IMAGE_NAME:latest .

      - name: Deploy to Container Apps
        run: |
          az containerapp update \
            --name $ACA_NAME \
            --resource-group $RESOURCE_GROUP \
            --image $ACR_NAME.azurecr.io/$IMAGE_NAME:${{ github.sha }}
```

This is ONE deploy job instead of two. No publish profile secret needed — uses workload identity federation.

#### Step 5: Add Entra ID Easy Auth

ACA has built-in Easy Auth. Add to the container app Bicep:
```bicep
configuration: {
  // ... existing ingress config
  authConfigs: [{
    name: 'current'
    properties: {
      platform: { enabled: true }
      identityProviders: {
        azureActiveDirectory: {
          registration: {
            clientId: appRegistrationClientId
            openIdIssuer: 'https://login.microsoftonline.com/${subscription().tenantId}/v2.0'
          }
          validation: {
            allowedAudiences: [ 'api://${appRegistrationClientId}' ]
          }
        }
      }
    }
  }]
}
```

This replaces function key auth. The user's identity flows through as `X-MS-CLIENT-PRINCIPAL` header. Parse it in middleware to get the real userId instead of trusting the request body.

---

## Component-by-Component Options

### Database: Cosmos DB + Conversation Mode

The biggest cost decision isn't provisioned vs serverless for your data — it's whether you need Foundry-managed conversations at all. This is a **deploy-time parameter** that controls both the infrastructure and the code path.

#### The Core Question: Do You Need `enterprise_memory`?

Foundry Agent Service creates an `enterprise_memory` database in your BYO Cosmos with containers for conversation state (`thread-message-store`, `system-thread-message-store`, `agent-entity-store`). This requires **provisioned throughput at minimum 3,000 RU/s (~$175/month)** — and it's per client stamp. At 10 clients, that's $1,750/month just for Foundry's conversation storage, whether anyone is using it or not.

The alternative: your ConversationHandler already uses `PreviousResponseId` chaining for the tool call loop. You can extend this pattern to handle ALL multi-turn continuity by storing the last response ID in your own `discovery` database. You lose Foundry-managed conversation history but keep everything else: the agent, the tools, the knowledge extraction, the Responses API.

#### Three Deployment Profiles

Add a `conversationMode` parameter to the Bicep template. This controls what gets provisioned:

| Profile | Param value | Foundry Cosmos | Your Cosmos | Monthly Data Cost | Use Case |
|---------|-------------|---------------|-------------|------------------|----------|
| **Lightweight** | `lightweight` | NOT provisioned | Serverless | ~$2-3/mo | Dev, demos, pilots, cost-sensitive clients. Multi-turn via `PreviousResponseId` stored in your `discovery-sessions` container. |
| **Standard** | `standard` | Provisioned (3,000 RU/s autoscale) | Serverless (separate account) | ~$90-180/mo | Production clients who want Foundry-managed conversation history, portal conversation viewer, and future Foundry memory features. |
| **Full** | `full` | Provisioned (3,000 RU/s autoscale) | Same account (autoscale) | ~$175-200/mo | Enterprise clients who need everything in one account for compliance/governance simplicity. |

#### Bicep Implementation

```bicep
@description('Conversation persistence mode')
@allowed(['lightweight', 'standard', 'full'])
param conversationMode string = 'lightweight'

// Your discovery data — always serverless (unless full mode)
module discoveryDb 'modules/cosmos-db.bicep' = {
  name: 'deploy-discovery-cosmos'
  params: {
    accountName: '${baseName}-cosmos-${uniqueSuffix}'
    serverless: conversationMode != 'full'  // serverless for lightweight + standard
    // ... databases, containers
  }
}

// Foundry's enterprise_memory — only for standard/full
module foundryDb 'modules/cosmos-db-foundry.bicep' = if (conversationMode != 'lightweight') {
  name: 'deploy-foundry-cosmos'
  params: {
    // For 'standard': separate provisioned account for Foundry
    // For 'full': same account as discovery (provisioned)
    accountName: conversationMode == 'standard'
      ? '${baseName}-foundry-cosmos-${uniqueSuffix}'
      : '${baseName}-cosmos-${uniqueSuffix}'
    minThroughput: 3000
    autoscaleMax: 10000
  }
}
```

#### Code Changes for Lightweight Mode

The ConversationHandler needs a conditional path based on an environment variable:

```csharp
// In DiscoveryBotSettings:
public string ConversationMode { get; set; } = "lightweight"; // from CONVERSATION_MODE env var

// In ConversationHandler.HandleAsync:
if (_settings.ConversationMode == "lightweight")
{
    // No Foundry conversation — use PreviousResponseId chaining
    // Store/retrieve previousResponseId in discovery-sessions container
    var session = await _sessionStore.GetOrCreateAsync(request.ConversationId, request.ContextId);
    
    var responseClient = _projectClient.OpenAI.GetProjectResponsesClientForAgent(agentName);
    
    var options = new CreateResponseOptions();
    options.InputItems.Add(ResponseItem.CreateUserMessageItem(request.Message));
    if (session.LastResponseId is not null)
        options.PreviousResponseId = session.LastResponseId;
    
    var response = await responseClient.CreateResponseAsync(options, ct);
    
    // Store the response ID for next turn
    await _sessionStore.UpdateLastResponseIdAsync(session.Id, response.Value.Id);
    
    // ... tool call loop uses PreviousResponseId as before
}
else
{
    // Full Foundry conversation — existing code path
    var conversation = await _projectClient.OpenAI.Conversations
        .CreateProjectConversationAsync(creationOptions, ct);
    var responseClient = _projectClient.OpenAI.GetProjectResponsesClientForAgent(
        agentName, conversation.Value);
    // ... existing conversation-bound flow
}
```

**What you keep in lightweight mode:** Agent definition with tools, Responses API, function tool calling loop, knowledge extraction, user profiles, questionnaires, session tracking — everything in your `discovery` database works identically.

**What you lose in lightweight mode:** Foundry portal conversation viewer (you can build your own from session data), Foundry-managed memory across sessions (you'd need to implement your own), and any future Foundry features that depend on managed conversations.

#### Cost Guardrails

The real problem isn't $175/month — it's $175/month × 12 months × "forgot about it." Add these safety nets:

**1. Deploy-time budget tag (Bicep)**
```bicep
param monthlyBudgetLimit int = 200  // USD

resource budgetAlert 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: '${baseName}-budget'
  properties: {
    amount: monthlyBudgetLimit
    category: 'Cost'
    timeGrain: 'Monthly'
    timePeriod: { startDate: '${utcNow('yyyy-MM')}-01' }
    notifications: {
      actual80: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        contactEmails: alertEmails
      }
      actual100: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        contactEmails: alertEmails
      }
    }
  }
}
```

**2. Idle stamp detection (alert rule)**
```bicep
// Alert if a stamp has zero conversations for 14 days
resource idleStampAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: '${baseName}-idle-stamp'
  location: location
  properties: {
    severity: 3
    evaluationFrequency: 'P1D'
    windowSize: 'P14D'
    criteria: {
      allOf: [{
        query: 'customMetrics | where name == "discovery.conversations.created" | summarize total=sum(value) | where total == 0'
        timeAggregation: 'Count'
        operator: 'LessThanOrEqual'
        threshold: 0
      }]
    }
    actions: { actionGroups: [actionGroupId] }
    description: 'This stamp has had zero conversations in the last 14 days. Consider deprovisioning.'
  }
}
```

**3. Stamp expiry tag (operational)**
Add a `decommissionBy` tag to every stamp's resource group. An Azure Policy or scheduled runbook scans for stamps past their expiry and sends a warning. If no action is taken within 7 days, auto-delete.

```bicep
param decommissionBy string = '' // e.g., '2026-06-30' — empty = no auto-expire

param tags object = {
  project: 'discovery-bot-v2'
  client: prefix
  environment: suffix
  conversationMode: conversationMode
  decommissionBy: decommissionBy
  monthlyBudgetUsd: string(monthlyBudgetLimit)
}
```

#### Updated Cost Comparison

| Profile | Compute (ACA) | Cosmos | AI Search | Storage | Observability | Total/mo |
|---------|--------------|--------|-----------|---------|--------------|----------|
| **Lightweight** | ~$0 (free tier) | ~$2.50 (serverless) | $0 (free) | ~$1 | ~$0 | **~$8.50** |
| **Standard** | ~$0 (free tier) | ~$2.50 + $90-175 (Foundry) | $0 (free) | ~$1 | ~$0 | **~$95-180** |
| **Full** | ~$5-15 (min-replica=1) | ~$175-200 (autoscale) | ~$75 (Basic) | ~$1-5 | ~$0-25 | **~$260-320** |

At 10 client stamps:
- All lightweight: **~$85/month** total
- All standard: **~$950-1,800/month** total
- Mixed (2 standard + 8 lightweight): **~$258-428/month** total

The deploy-time parameter means you start every stamp as lightweight and upgrade to standard only when the client needs Foundry-managed conversations. The budget alert and idle detection catch the ones nobody upgraded but also nobody turned off.

### Search: AI Search

| Option | Monthly Cost | Pros | Cons |
|--------|-------------|------|------|
| **AI Search Free** | $0 | Free. 50MB storage, 3 indexes. Fine for dev/pilot. | Limited scale. No SLA. |
| AI Search Basic (current) | ~$75/mo | 2GB storage, 15 indexes. SLA. | Fixed cost regardless of usage. Expensive for low-traffic stamps. |
| **Skip AI Search entirely** | $0 | Eliminate a resource and its RBAC. Use Cosmos queries for knowledge retrieval. | Loses semantic/vector search. Cosmos queries are less flexible for full-text search. |
| Azure PostgreSQL Flexible + pgvector | ~$13/mo (B1ms) | Vector search + relational in one. Could replace BOTH Cosmos and AI Search. | Different SDK. Migration effort. Not a Microsoft-native agent service integration. |

**Recommendation for dev/pilot:** Use **AI Search Free tier**. For production stamps with real data volume, Basic. For Marketplace, make the SKU a parameter so clients choose. If you want maximum simplicity, consider whether you can skip AI Search entirely and use Cosmos queries with composite indexes — the knowledge items are already partitioned by contextId, and most queries are scoped to a single context.

### Storage: Blob Storage

No change needed. Blob Storage is already cheap (~$0.02/GB/month), serverless by nature, and the current setup is fine. The only change is that the web UI files move INTO the container (served via ASP.NET `UseStaticFiles`), so the `$web` static website container and its separate deploy job go away.

### Observability: App Insights

No change needed. App Insights has a generous free tier (5GB/month ingestion), and OpenTelemetry works identically from ACA as it does from Functions. The `APPLICATIONINSIGHTS_CONNECTION_STRING` env var is the only config needed.

### Auth: Multi-Audience Authentication

Discovery Bot has three distinct audiences with different auth requirements. The `authMode` is a deploy-time parameter on each stamp, just like `conversationMode`.

#### Audience 1: GT Operators (Management Plane)

This is solved: `us.gt.com` Entra ID via Easy Auth on the management plane ACA. Only your people, only your tenant. App roles: `Platform.Operator` (provision/deprovision/configure stamps) and `Platform.Viewer` (fleet dashboard, read-only).

#### Audience 2: Client Stakeholders (Web Chat)

The people being interviewed. They're external to your tenant, may not have Azure AD accounts, and you need zero-friction onboarding. Auth mode is configurable per stamp:

| Auth Mode | Param Value | User Experience | Security Level | Best For |
|-----------|-------------|----------------|---------------|----------|
| **Magic link** | `magic_link` | Enter email → click link in inbox → session cookie for 7-30 days | Medium-high (verified email, domain allowlist) | Most client engagements |
| **Invite code** | `invite_code` | Click invite URL → enter name/email → session | Low (self-asserted email, shareable link) | Quick demos, pilots |
| **Entra External ID** | `entra_external` | Click sign in → email OTP or federated SSO → token | High (Microsoft-managed, MFA support) | Sensitive/regulated engagements |

**Recommended default: `magic_link`**

**Magic Link Implementation:**

```bicep
@description('Client auth mode for the chat interface')
@allowed(['magic_link', 'invite_code', 'entra_external'])
param authMode string = 'magic_link'

@description('Allowed email domains for client access (e.g., ["acme.com", "acmeconsulting.com"])')
param allowedEmailDomains array = []

@description('Session duration in days')
param sessionDurationDays int = 7
```

The flow:

1. User opens the chat URL, sees "enter your work email"
2. App checks the domain against `allowedEmailDomains` — rejects if not in the list
3. App generates a one-time token, stores it in Cosmos (TTL: 15 minutes), and sends a magic link via Azure Communication Services (or Entra External ID's email OTP)
4. User clicks the link, app validates the token, creates a signed JWT
5. JWT is set as an `HttpOnly`, `Secure`, `SameSite=Strict` cookie with the configured expiry
6. Every API request validates the cookie server-side and extracts the verified email as the userId
7. No password, no account creation, no app install

**Cookie structure:**
```json
{
  "sub": "sarah.chen@acme.com",
  "stampId": "acme-prod",
  "contextId": "tech-consulting-001",
  "iat": 1711324800,
  "exp": 1711929600
}
```

Signed with a per-stamp secret stored as an ACA secret (not in env vars, not in code). Validated server-side via ASP.NET Core `AddAuthentication().AddJwtBearer()` middleware.

**Code changes:**

```csharp
// In Program.cs — add auth middleware based on AUTH_MODE
if (settings.AuthMode == "magic_link")
{
    builder.Services.AddAuthentication("MagicLink")
        .AddJwtBearer("MagicLink", options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(settings.SessionSigningKey)),
            };
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    // Read from cookie instead of Authorization header
                    ctx.Token = ctx.Request.Cookies["discovery_session"];
                    return Task.CompletedTask;
                }
            };
        });
    builder.Services.AddAuthorization();
}

// New endpoints for the magic link flow:
app.MapPost("/auth/request", async (EmailRequest req, MagicLinkService links) =>
{
    if (!links.IsAllowedDomain(req.Email))
        return Results.Forbid();
    await links.SendMagicLinkAsync(req.Email);
    return Results.Ok(new { status = "sent" });
});

app.MapGet("/auth/verify", async (string token, MagicLinkService links, HttpContext ctx) =>
{
    var email = await links.ValidateTokenAsync(token);
    if (email is null) return Results.Unauthorized();
    
    var jwt = links.CreateSessionJwt(email);
    ctx.Response.Cookies.Append("discovery_session", jwt, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        MaxAge = TimeSpan.FromDays(settings.SessionDurationDays),
    });
    return Results.Redirect("/");
});

// All other endpoints get [Authorize]:
app.MapPost("/api/conversation", [Authorize] async (...) => { ... });
// userId comes from the validated token, not the request body:
var userId = ctx.User.FindFirst("sub")?.Value;
```

**For invite code mode:** Same cookie mechanism, but the "verification" step is just validating the invite code instead of sending an email. The code maps to a stamp + allowed domains in the central Cosmos registry. Weaker security but faster setup.

**For Entra External ID mode:** ACA Easy Auth handles everything. The app registration points to an External ID tenant configured with email OTP. The user gets a Microsoft-managed sign-in page. The identity flows through `X-MS-CLIENT-PRINCIPAL`. No custom cookie handling needed.

#### Audience 3: Client Stakeholders (Teams Bot)

Teams provides identity automatically. The Bot Framework activity carries the user's Entra UPN, display name, and tenant ID. No additional auth is needed — the user is already authenticated by Teams. The bot reads identity from `activity.From.AadObjectId` and `activity.From.Name`.

The main consideration: installing a Teams bot in a client's tenant requires their IT admin to approve it. This is a separate deploy step managed through the management portal — the operator publishes the bot to the client's Teams admin center.

#### Auth in the Bicep Template

```bicep
@description('Client auth mode')
@allowed(['magic_link', 'invite_code', 'entra_external'])
param authMode string = 'magic_link'

@description('Allowed email domains')
param allowedEmailDomains array = []

@description('Session duration in days')
param sessionDurationDays int = 7

@description('Entra External ID tenant (only for entra_external mode)')
param externalIdTenant string = ''

// Container app env vars include auth config
env: [
  { name: 'AUTH_MODE', value: authMode }
  { name: 'ALLOWED_EMAIL_DOMAINS', value: join(allowedEmailDomains, ',') }
  { name: 'SESSION_DURATION_DAYS', value: string(sessionDurationDays) }
  { name: 'SESSION_SIGNING_KEY', secretRef: 'session-key' }  // ACA secret, not env var
]
```

#### Updated Stamp Parameters

The stamp template now has three deploy-time decisions:

```bicep
// What conversation persistence mode?
param conversationMode string = 'lightweight'     // lightweight | standard | full

// How do client stakeholders authenticate?
param authMode string = 'magic_link'              // magic_link | invite_code | entra_external

// Which email domains can access this stamp?
param allowedEmailDomains array = []              // e.g., ['acme.com']
```

These are set by the GT operator through the management portal when provisioning a stamp. The same container image handles all combinations.

### CI/CD: GitHub Actions

| Option | Pros | Cons |
|--------|------|------|
| **ACR Build + ACA Update** (RECOMMENDED) | Single job. No publish profile. `az acr build` builds the image in the cloud (no local Docker needed). `az containerapp update` deploys. Workload identity federation for auth. | Requires ACR (~$5/mo Basic). |
| GitHub Container Registry (ghcr.io) | Free for public repos. No ACR needed. | ACA needs credentials to pull from ghcr.io. More complex auth setup. |
| azd deploy | Integrated with azd workflow. | Adds azd as a dependency. Less transparent than raw CLI. |

**Recommendation:** `az acr build` + `az containerapp update` in a single GitHub Actions job. Simplest, most transparent, no local Docker required.

---

## What Gets Removed

| Item | Why |
|------|-----|
| `Microsoft.Azure.Functions.Worker` packages (3) | Replaced by ASP.NET Core built-in hosting |
| `host.json` | Functions-specific config. Not needed in ASP.NET Core. |
| `function-app.bicep` | Replaced by `container-app.bicep` |
| All `[Function]` and `[HttpTrigger]` attributes | Replaced by `app.MapGet/MapPost` |
| Separate Function class files | Route handlers move into Program.cs or thin controller classes |
| `infra/scripts/post-provision.sh` | All config is inline in the Bicep container app definition. No imperative post-provision step. |
| `scripts/new-client.sh` | Replace with a single deployment script or azd |
| Blob static website deploy job | Web UI served from the container |
| Function key auth | Replaced by Entra ID Easy Auth |
| `AzureWebJobsStorage__accountName` + its storage account | Functions-specific. ACA doesn't need a dedicated storage account for its runtime. |

## What Gets Added

| Item | Why |
|------|-----|
| `Dockerfile` | Container image definition |
| `container-app.bicep` | ACA environment + app definition |
| ACR resource in `main.bicep` | Container registry for images (~$5/mo Basic) |
| `.dockerignore` | Keep the build context clean |
| Entra ID app registration (in Bicep or manual) | For Easy Auth |
| Workload identity federation (GitHub OIDC) | For CI/CD without secrets |

---

## Cost Comparison

### Per-Stamp Monthly Cost by Deployment Profile

| Component | Lightweight | Standard | Full |
|-----------|-----------|----------|------|
| Compute (ACA) | ~$0 (free tier) | ~$0 (free tier) | ~$5-15 (min-replica=1) |
| Cosmos (your data) | ~$2.50 (serverless) | ~$2.50 (serverless, separate account) | ~$25-50 (autoscale, shared account) |
| Cosmos (Foundry) | $0 (not provisioned) | ~$90-175 (provisioned, autoscale) | Included above |
| AI Search | $0 (free tier) | $0 (free tier) | ~$75 (Basic) |
| Storage | ~$1 | ~$1 | ~$1-5 |
| App Insights | ~$0 | ~$0 | ~$0-25 |
| ACR | ~$5 (shared across stamps) | ~$5 (shared) | ~$5 (shared) |
| **Total** | **~$8.50/mo** | **~$100-185/mo** | **~$210-370/mo** |

### Fleet Cost at Scale (10 stamps)

| Scenario | Monthly Total | Notes |
|----------|--------------|-------|
| All lightweight | ~$85 | Dev, demos, pilot fleet |
| Mixed (2 standard + 8 lightweight) | ~$270-440 | Typical: a couple of active clients, rest are pilots |
| All standard | ~$1,000-1,850 | All clients need Foundry conversations |
| All full | ~$2,100-3,700 | Enterprise tier everywhere |

### vs Current Architecture

| | Current (per stamp) | Lightweight | Savings |
|--|-------------------|------------|---------|
| Dev stamp | ~$100/mo | ~$8.50/mo | 92% |
| Prod stamp | ~$200/mo | ~$100-185/mo | 8-50% |

The lightweight profile is the default. Upgrade to standard when the client specifically needs Foundry-managed conversation history. The `conversationMode` parameter in the Bicep template controls what gets provisioned — no code branch needed at deploy time, just a different param value.

---

## Migration Order

### Phase 1: Containerize (1-2 days)
1. Create Dockerfile
2. Convert Program.cs from Functions host to WebApplication
3. Convert Function classes to minimal API routes
4. Add `app.UseStaticFiles()` and copy web/ to wwwroot/
5. Remove Functions NuGet packages
6. Remove host.json
7. Test locally with `docker build && docker run`
8. Verify all endpoints work identically

### Phase 2: Infrastructure (1 day)
1. Create `container-app.bicep` (replace function-app.bicep)
2. Add ACR to main.bicep
3. Move all app settings from post-provision.sh into container app env vars in Bicep
4. Remove post-provision.sh
5. Remove the Functions-specific storage account from function-app.bicep
6. Update role-assignments.bicep to use the ACA managed identity principal
7. Run `az bicep build` to validate

### Phase 3: Deploy & CI/CD (half day)
1. Build and push image to ACR: `az acr build --registry ACR --image discovery-bot:v1 .`
2. Deploy infrastructure: `az deployment group create` with updated Bicep
3. Verify the container app starts and all endpoints respond
4. Update GitHub Actions to single-job ACR build + ACA update
5. Remove old Functions deploy workflow

### Phase 4: Auth — Magic Link + Management Plane (1-2 days)
1. Create `MagicLinkService` — generates one-time tokens, stores in Cosmos (TTL 15min), sends email via Azure Communication Services
2. Create `/auth/request` and `/auth/verify` endpoints
3. Add JWT cookie middleware to Program.cs with `AUTH_MODE` switch
4. Update web/index.html login screen — "enter your work email" form, domain validation client-side, magic link sent state, redirect handler
5. Add `[Authorize]` to all API endpoints, extract userId from `ctx.User.FindFirst("sub")`
6. Remove self-asserted userId from `ConversationRequest` — it now comes from the verified token
7. Add `allowedEmailDomains` and `authMode` to Bicep container app env vars
8. Add invite code fallback for demos (simpler flow, same cookie mechanism)
9. Test: request magic link → click → cookie set → conversation works → cookie expires → re-auth required

### Phase 5: Cost Optimization (optional, after stable)
1. Switch Cosmos to serverless (requires new account) or autoscale
2. Switch AI Search to Free tier for dev stamps
3. Evaluate whether AI Search is needed at all vs Cosmos queries

### Phase 6: Management Plane (2-3 days)
A separate application in GT's subscription that provides centralized control over all client stamps.

1. **Stamp registry** — Central Cosmos (serverless, ~$2/mo) in GT's subscription stores: stamp name, resource group, subscription, conversation mode, auth mode, allowed domains, deployed version, created date, last activity, monthly cost estimate
2. **Provisioning portal** — ASP.NET Core app on ACA behind `us.gt.com` Entra ID Easy Auth. UI where an operator fills in: client name, Azure subscription + region, conversation mode, auth mode, allowed email domains, session duration. Clicking "Deploy" calls Azure Resource Manager SDK to create the resource group and run the Bicep template with the selected parameters. Deployment status is tracked in the stamp registry.
3. **Template library** — Central store of reusable questionnaires and discovery contexts. Operator can push templates to any stamp via its admin API (`POST /api/manage/questionnaire`, `POST /api/manage/context`). Templates are versioned in the central Cosmos.
4. **Fleet dashboard** — Aggregates telemetry from all stamps. Shows: active stamps, idle stamps (14+ days), cost per stamp (from Azure Cost Management API), knowledge items extracted per stamp, quality scores, last activity timestamp. Alerts surface here.
5. **Stamp lifecycle** — Upgrade conversation mode, rotate session signing keys, update allowed email domains, deprovision (with confirmation). Deprovisioning deletes the resource group via ARM SDK.
6. **Auth** — `us.gt.com` Entra ID only. App roles: `Platform.Operator` (full access) and `Platform.Viewer` (dashboard + template library, no provisioning).

**Tech stack:** Separate repo (`disco-bot-management`), separate ACA container, separate Bicep template. Shares the same Entra ID app registration as the GT operator role. Central Cosmos is serverless (~$2/mo). The management plane itself costs ~$10/month (ACA free tier + serverless Cosmos + shared ACR).

**What it replaces:** The `scripts/new-client.sh` script, manual `az` CLI commands for provisioning, manual Cosmos document uploads for questionnaires/contexts, and the "who's using what" spreadsheet that doesn't exist yet.

---

## Marketplace Implications

Containerization is a prerequisite for Marketplace. The target architecture maps directly to a Marketplace ARM template:

1. ARM template creates: ACA environment, ACR reference (your public ACR), Cosmos, Storage, App Insights
2. Customer selects a **deployment profile** (Lightweight, Standard, Full) which controls Cosmos provisioning and conversation mode
3. Customer selects an **auth mode** (Magic Link, Invite Code, Entra External ID) and enters allowed email domains
4. Customer fills in: resource group, region, Foundry project endpoint, model deployment name
5. Template pulls your published image from your public ACR
6. All config (including `CONVERSATION_MODE`, `AUTH_MODE`, `ALLOWED_EMAIL_DOMAINS`) is injected as container env vars
7. Budget alert is auto-created at the customer's specified threshold
8. Idle stamp detection alert is deployed (warns after 14 days of zero conversations)

The Marketplace `createUiDefinition.json` presents the three deployment profiles and three auth modes as radio buttons with cost estimates, so the customer understands the implications before deploying.

For GT-managed deployments (not self-service Marketplace), the management plane handles all of this through its provisioning portal — the operator never touches CLI tools or Bicep files directly.

No CLI tools, no scripts, no manual steps. Whether deployed via Marketplace or the management portal, the customer gets a running Discovery Bot with auth, cost guardrails, and observability built in.

---

## Complete Stamp Parameter Matrix

Every client stamp is defined by these deploy-time parameters. The management portal exposes these as a form; the Marketplace exposes them as `createUiDefinition.json` fields; direct Bicep users set them in their `.bicepparam` file.

```bicep
// Identity
param prefix string                        // Client name (e.g., 'acme')
param suffix string = 'prod'               // Environment (dev, staging, prod)

// Conversation persistence
param conversationMode string = 'lightweight'  // lightweight | standard | full

// Client authentication
param authMode string = 'magic_link'           // magic_link | invite_code | entra_external
param allowedEmailDomains array = []           // e.g., ['acme.com', 'acme-consulting.com']
param sessionDurationDays int = 7              // Magic link session TTL

// Model
param primaryModelName string = 'gpt-4o'
param primaryModelCapacity int = 50

// Cost guardrails
param monthlyBudgetLimit int = 200             // USD, triggers alert at 80% and 100%
param decommissionBy string = ''               // Optional auto-expire date

// Region + access
param location string = resourceGroup().location
param enablePublicAccess bool = true           // false for prod (private endpoints)
```
