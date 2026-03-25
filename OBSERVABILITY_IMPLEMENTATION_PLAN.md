# Discovery Bot v2 — Observability Implementation Plan

## Architecture Principle
Everything flows to a single App Insights instance per client stamp. Foundry-native traces, custom business metrics, and alert rules all converge in one workspace. The observability layer is deployable independently (its own Bicep module + dashboard files) but tightly coupled to the runtime via shared App Insights.

## Deployment Model
The observability stack should be:
- **Separately deployable** — its own Bicep module (`infra/modules/observability.bicep`) that can be added to an existing stamp without redeploying the app
- **Dashboard as code** — the admin dashboard page (`web/dashboard.html`) reads from App Insights via the REST API, not a separate backend
- **Alert rules in Bicep** — every stamp gets the same monitoring baseline automatically
- **Custom evaluators registered via SDK** — a one-time setup script, not part of the main app startup

---

## Phase 1: Instrumentation (Day 1)

### Task 1.1: Wire OpenTelemetry into Program.cs

The `Azure.Monitor.OpenTelemetry.AspNetCore` package is already in the csproj. Add the instrumentation to Program.cs:

```csharp
// In the HostBuilder chain, after ConfigureFunctionsWebApplication():
.ConfigureServices((context, services) =>
{
    // Add OpenTelemetry with Azure Monitor exporter
    services.AddOpenTelemetry()
        .UseAzureMonitor(options =>
        {
            options.ConnectionString = settings.AppInsightsConnectionString;
        });
    
    // ... rest of existing DI registrations
})
```

This auto-instruments HTTP requests, dependencies (Cosmos, AI Search, Storage), and provides the ILogger integration that sends structured logs to App Insights.

### Task 1.2: Define Custom Metrics

Create a new file: `src/DiscoveryAgent/Telemetry/DiscoveryMetrics.cs`

```csharp
using System.Diagnostics.Metrics;

namespace DiscoveryAgent.Telemetry;

public class DiscoveryMetrics
{
    public static readonly Meter Meter = new("DiscoveryBot", "1.0.0");
    
    // Knowledge extraction
    public static readonly Counter<int> KnowledgeItemsExtracted = 
        Meter.CreateCounter<int>("discovery.knowledge.items_extracted", "items", "Knowledge items extracted per tool call");
    public static readonly Histogram<double> ExtractionConfidence = 
        Meter.CreateHistogram<double>("discovery.knowledge.confidence", "score", "Confidence distribution of extracted items");
    public static readonly Counter<int> ExtractionFailures = 
        Meter.CreateCounter<int>("discovery.knowledge.extraction_failures", "failures", "Failed extraction tool calls");
    
    // Session quality
    public static readonly Histogram<double> SessionDuration = 
        Meter.CreateHistogram<double>("discovery.session.duration_seconds", "seconds", "Session duration");
    public static readonly Histogram<int> MessagesPerSession = 
        Meter.CreateHistogram<int>("discovery.session.message_count", "messages", "Messages per session");
    public static readonly Counter<int> SectionsCompleted = 
        Meter.CreateCounter<int>("discovery.questionnaire.sections_completed", "sections", "Questionnaire sections completed");
    
    // Tool calls
    public static readonly Counter<int> ToolCallsTotal = 
        Meter.CreateCounter<int>("discovery.tools.calls_total", "calls", "Total tool calls by function name");
    public static readonly Histogram<double> ToolCallDuration = 
        Meter.CreateHistogram<double>("discovery.tools.duration_ms", "ms", "Tool call execution time");
    
    // Conversations
    public static readonly Counter<int> ConversationsCreated = 
        Meter.CreateCounter<int>("discovery.conversations.created", "conversations", "New conversations started");
    public static readonly Counter<int> ConversationsResumed = 
        Meter.CreateCounter<int>("discovery.conversations.resumed", "conversations", "Existing conversations resumed");
    
    // Errors
    public static readonly Counter<int> AgentErrors = 
        Meter.CreateCounter<int>("discovery.errors", "errors", "Agent errors by type");
}
```

### Task 1.3: Instrument ConversationHandler

Add metric recording to the key points in `ConversationHandler.HandleAsync`:

```csharp
// After creating a new conversation:
DiscoveryMetrics.ConversationsCreated.Add(1, new KeyValuePair<string, object?>("contextId", request.ContextId ?? "default"));

// After resuming:
DiscoveryMetrics.ConversationsResumed.Add(1);

// In the tool call loop, when processing extract_knowledge results:
DiscoveryMetrics.KnowledgeItemsExtracted.Add(ids.Count, 
    new KeyValuePair<string, object?>("contextId", contextId));
foreach (var item in args.Items)
    DiscoveryMetrics.ExtractionConfidence.Record(item.Confidence);

// When a tool call is processed:
var sw = Stopwatch.StartNew();
var result = await ExecuteFunctionAsync(...);
sw.Stop();
DiscoveryMetrics.ToolCallsTotal.Add(1, new KeyValuePair<string, object?>("function", functionCall.FunctionName));
DiscoveryMetrics.ToolCallDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("function", functionCall.FunctionName));

// On extraction failure:
DiscoveryMetrics.ExtractionFailures.Add(1);

// On section complete:
DiscoveryMetrics.SectionsCompleted.Add(1, new KeyValuePair<string, object?>("sectionId", sectionId));
```

### Task 1.4: Add Readiness Health Endpoint

Create `src/DiscoveryAgent/Functions/HealthReadyFunction.cs`:

```csharp
[Function("HealthReady")]
public async Task<IActionResult> Run(
    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/ready")] HttpRequest req)
{
    var checks = new Dictionary<string, string>();
    
    // Check Cosmos
    try { 
        await _cosmosClient.ReadAccountAsync(); 
        checks["cosmos"] = "ok"; 
    } catch { checks["cosmos"] = "failed"; }
    
    // Check AI Search
    try { 
        await _searchClient.GetDocumentCountAsync(); 
        checks["aiSearch"] = "ok"; 
    } catch { checks["aiSearch"] = "failed"; }
    
    // Check Storage
    try { 
        await _blobClient.GetPropertiesAsync(); 
        checks["storage"] = "ok"; 
    } catch { checks["storage"] = "failed"; }
    
    var allHealthy = checks.Values.All(v => v == "ok");
    return allHealthy 
        ? new OkObjectResult(new { status = "healthy", checks, timestamp = DateTime.UtcNow })
        : new ObjectResult(new { status = "degraded", checks, timestamp = DateTime.UtcNow }) { StatusCode = 503 };
}
```

### Task 1.5: Replace DefaultAzureCredential in Production

In Program.cs, use `ManagedIdentityCredential` for production to avoid credential probing latency:

```csharp
var credential = builder.Environment.IsProduction()
    ? new ManagedIdentityCredential()
    : new DefaultAzureCredential();
```

(For the Container Apps migration, this becomes an environment variable check instead.)

---

## Phase 2: Evaluators & Alerts (Day 2)

### Task 2.1: Register Custom Evaluators

Create a one-time setup script: `scripts/register-evaluators.ps1`

This uses the Azure AI Projects SDK to register three custom evaluators in the Foundry project. These run on sampled production traffic via continuous evaluation.

**Task Adherence Evaluator** (LLM-as-a-judge):
- Scores each agent response 1-5 on whether it stays in discovery/questioning mode vs. drifting into answer-giving mode
- Prompt template references the discovery methodology from instructions.md
- Threshold: alert if average drops below 3.0

**Knowledge Quality Evaluator** (code-based):
- Checks extracted knowledge items for: content length > 20 chars, category matches content, confidence is calibrated
- Runs on the extract_knowledge tool call outputs
- Threshold: alert if quality rate drops below 80%

**Questionnaire Fidelity Evaluator** (code-based):
- When a questionnaire context is active, checks section completion order, question coverage percentage, and whether the bot allowed elaboration
- Runs on sessions with a contextId that maps to a questionnaire
- Threshold: alert if coverage drops below 70%

### Task 2.2: Configure Continuous Evaluation

Via the SDK or Foundry portal:
- Sampling rate: 10% of production traffic (adjustable per stamp)
- Built-in evaluators: Groundedness, Coherence, Relevance, Safety (hate, violence, prompt injection)
- Custom evaluators: Task Adherence, Knowledge Quality, Questionnaire Fidelity
- Results flow to the same App Insights instance

### Task 2.3: Add Alert Rules to Bicep

Create `infra/modules/observability.bicep`:

```bicep
param appInsightsId string
param actionGroupId string  // Teams webhook or email

// Quality alerts
resource groundednessAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-groundedness-low'
  location: location
  properties: {
    severity: 2
    evaluationFrequency: 'PT15M'
    windowSize: 'PT1H'
    criteria: {
      allOf: [{
        query: 'customMetrics | where name == "ai.evaluation.groundedness" | summarize avg(value) by bin(timestamp, 1h) | where avg_value < 3.5'
        timeAggregation: 'Average'
        operator: 'LessThan'
        threshold: 3.5
      }]
    }
    actions: { actionGroups: [actionGroupId] }
  }
}

// Operational alerts
resource successRateAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = { ... }
resource latencyAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = { ... }
resource extractionFailureAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = { ... }

// Safety alerts  
resource safetyAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = { ... }
resource redTeamAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = { ... }
```

Wire this into main.bicep as an optional module:
```bicep
param enableObservability bool = true

module observability 'modules/observability.bicep' = if (enableObservability) {
  name: 'deploy-observability'
  params: {
    appInsightsId: appInsights.outputs.appInsightsId
    actionGroupId: alertActionGroup.outputs.actionGroupId
  }
}
```

### Task 2.4: Schedule Red Teaming

Via the Foundry portal or SDK:
- Weekly adversarial scan against the agent
- Tests for: prompt injection, jailbreak attempts, PII extraction, boundary violations
- Failed scans create alerts in the same alert pipeline

---

## Phase 3: Dashboard (Day 3)

### Task 3.1: Create the Observability Dashboard Page

Create `web/dashboard.html` — a standalone page that queries App Insights via the REST API and renders metrics in real time.

**Architecture decision:** The dashboard should NOT require a backend API endpoint. It queries App Insights directly using the user's Entra ID token (same auth as the admin page). This keeps it independently deployable — you can update the dashboard without redeploying the app.

**Sections:**

1. **Health Status** — Green/amber/red indicators for each dependency (Cosmos, AI Search, Storage, Foundry). Polls `/health/ready` every 30 seconds.

2. **Conversation Activity** — Line chart of conversations created/resumed over time. Bar chart of messages per session. Source: custom metrics `discovery.conversations.*` and `discovery.session.*`.

3. **Knowledge Extraction** — Counter of total items extracted (today, this week). Category breakdown pie chart (fact/opinion/decision/requirement/concern). Confidence distribution histogram. Source: custom metrics `discovery.knowledge.*`.

4. **Agent Quality** — Gauge charts for Groundedness, Coherence, Task Adherence scores from continuous evaluation. Trend lines showing score changes over time. Source: App Insights `customMetrics` where name starts with `ai.evaluation.*`.

5. **Tool Call Performance** — Table showing each function tool's call count, average duration, and failure rate. Source: custom metrics `discovery.tools.*`.

6. **Alerts** — Recent fired alerts with severity, timestamp, and link to the App Insights trace. Source: Azure Monitor REST API.

**Tech stack:** Vanilla HTML/CSS/JS with Chart.js for visualizations. Same auth pattern as the existing admin page (function key or Entra ID token in header). No build step, no framework.

**Navigation:** Add a "Dashboard" link in both the chat UI and admin page headers. The dashboard links back to both.

### Task 3.2: App Insights Query Patterns

The dashboard queries App Insights using the [Query REST API](https://learn.microsoft.com/en-us/rest/api/application-insights/query/get):

```
GET https://api.applicationinsights.io/v1/apps/{appId}/query?query={kusto}
Authorization: Bearer {token}
```

Key Kusto queries for each dashboard section:

```kusto
// Knowledge extraction rate (last 24h)
customMetrics
| where name == "discovery.knowledge.items_extracted"
| where timestamp > ago(24h)
| summarize total=sum(value) by bin(timestamp, 1h)

// Category breakdown
customMetrics
| where name == "discovery.knowledge.items_extracted"
| where timestamp > ago(7d)
| extend category = tostring(customDimensions.category)
| summarize count=sum(value) by category

// Confidence distribution
customMetrics
| where name == "discovery.knowledge.confidence"
| where timestamp > ago(7d)
| summarize percentiles(value, 25, 50, 75, 90)

// Tool call performance
customMetrics
| where name == "discovery.tools.duration_ms"
| where timestamp > ago(24h)
| extend fn = tostring(customDimensions.function)
| summarize avg_ms=avg(value), p95_ms=percentile(value, 95), calls=count() by fn

// Session drop-off
customMetrics
| where name == "discovery.session.message_count"
| where timestamp > ago(7d)
| summarize count() by bin(value, 5)

// Evaluation scores (from Foundry continuous eval)
customMetrics
| where name startswith "ai.evaluation."
| where timestamp > ago(7d)
| summarize avg(value) by name, bin(timestamp, 1d)
```

---

## Phase 4: Fleet View (Future — Post-Marketplace)

When you have multiple client stamps deployed, add a central observability layer:

- **Central App Insights workspace** in your (operator) subscription
- **AI Gateway** in Foundry routes agent traces from all client stamps to the central workspace
- **Fleet dashboard** shows cross-client metrics: cost per client, quality scores by client, aggregate extraction rates
- **Per-client dashboards** remain isolated in the client's own App Insights

This is a post-Marketplace concern — don't build it until you have 3+ client stamps running.

---

## File Changes Summary

| File | Action | Purpose |
|------|--------|---------|
| `src/DiscoveryAgent/Telemetry/DiscoveryMetrics.cs` | Create | Custom metric definitions |
| `src/DiscoveryAgent/Handlers/ConversationHandler.cs` | Edit | Add metric recording |
| `src/DiscoveryAgent/Functions/HealthReadyFunction.cs` | Create | Readiness probe |
| `src/DiscoveryAgent/Program.cs` | Edit | Wire OpenTelemetry, credential hardening |
| `web/dashboard.html` | Create | Observability dashboard |
| `infra/modules/observability.bicep` | Create | Alert rules, action groups |
| `infra/main.bicep` | Edit | Wire observability module |
| `scripts/register-evaluators.ps1` | Create | One-time evaluator setup |

## Dependencies
- `Azure.Monitor.OpenTelemetry.AspNetCore` — already in csproj
- `System.Diagnostics.Metrics` — built into .NET 9
- Chart.js (CDN) — for dashboard visualizations
- App Insights REST API — for dashboard queries (no additional backend needed)

## Estimated Effort
- Phase 1 (Instrumentation): 1 day
- Phase 2 (Evaluators + Alerts): 1 day  
- Phase 3 (Dashboard): 1 day
- Phase 4 (Fleet View): future, post-Marketplace
