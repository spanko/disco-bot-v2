# disco-bot-v2: Claude Code Session Instructions

## Project Context
This is a Discovery Bot built on Microsoft Foundry Agent Service (GA API). The repo is at `spanko/disco-bot-v2`. It's a .NET 9 Azure Functions app that creates a conversational agent using the Foundry Responses API with function tool calling for knowledge extraction, user profile capture, and questionnaire tracking.

## Critical: Resolved NuGet Package Versions
These are the ACTUAL resolved versions. Do NOT guess type names from training data — always verify against these packages by running `dotnet build` after changes.

| Package | Resolved Version |
|---------|-----------------|
| Azure.AI.Projects | 2.0.0-beta.2 |
| Azure.AI.Extensions.OpenAI | 2.0.0-beta.1 |
| Azure.AI.OpenAI | 2.1.0 |
| Azure.Identity | 1.19.0 |

## SDK Type Map (discovered through build errors)
These beta packages have type names that differ from the docs. Here's what ACTUALLY works:

| Type | Namespace / Using | Notes |
|------|-------------------|-------|
| `PromptAgentDefinition` | `Azure.AI.Projects.Agents` | NOT in top-level `Azure.AI.Projects` |
| `CreateResponseOptions` | `Azure.AI.Extensions.OpenAI` or `OpenAI.Responses` | Constructor: `(string conversationId, IEnumerable<ResponseItem> inputItems)` — conversation ID is the FIRST param |
| `ResponseResult` | `OpenAI.Responses` | The response type (was renamed from `OpenAIResponse`) |
| `FunctionCallResponseItem` | `OpenAI.Responses` | Check `item is FunctionCallResponseItem` in output loop |
| `ResponseTool.CreateFunctionTool(...)` | `OpenAI.Responses` | For building function tool definitions |
| `AIProjectClient` | `Azure.AI.Projects` | Main entry point |
| `ProjectResponsesClient` | `Azure.AI.Extensions.OpenAI` | From `_projectClient.OpenAI.GetProjectResponsesClientForAgent(agentName)` |
| `ProjectConversationCreationOptions` | `Azure.AI.Extensions.OpenAI` | For creating conversations |
| `AgentVersion` | `Azure.AI.Projects.Agents` | Returned wrapped in `ClientResult<AgentVersion>` — use `.Value` to unwrap |

## Key API Patterns

### Creating an agent version
```csharp
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;

var definition = new PromptAgentDefinition(modelDeploymentName)
{
    Instructions = "...",
};
definition.Tools.Add(ResponseTool.CreateFunctionTool(...));

var result = await _projectClient.Agents.CreateAgentVersionAsync(
    agentName: "my-agent",
    options: new(definition),
    cancellationToken: ct);

var agentVersion = result.Value; // MUST unwrap ClientResult<AgentVersion>
Console.WriteLine(agentVersion.Name);
Console.WriteLine(agentVersion.Version);
```

### Creating a response with conversation binding
```csharp
using Azure.AI.Extensions.OpenAI;

var responseClient = _projectClient.OpenAI.GetProjectResponsesClientForAgent(agentName);

// Constructor is (string conversationId, IEnumerable<ResponseItem> inputItems)
// NO AgentConversationId property — conversation ID goes in the constructor
var options = new CreateResponseOptions(
    conversationId,
    [ResponseItem.CreateUserMessageItem("Hello")]);

var response = await responseClient.CreateResponseAsync(options, ct);
var result = response.Value; // ResponseResult
Console.WriteLine(result.GetOutputText());
```

### Function tool call loop
```csharp
var currentResponse = response.Value;

while (currentResponse.OutputItems.Any(item => item is FunctionCallResponseItem))
{
    var toolOutputs = new List<ResponseItem>();
    foreach (var item in currentResponse.OutputItems)
    {
        toolOutputs.Add(item);
        if (item is FunctionCallResponseItem functionCall)
        {
            var result = ExecuteFunction(functionCall);
            toolOutputs.Add(ResponseItem.CreateFunctionCallOutputItem(
                functionCall.CallId, result));
        }
    }

    var followUp = new CreateResponseOptions(conversationId, toolOutputs)
    {
        PreviousResponseId = currentResponse.Id,
    };

    var nextResponse = await responseClient.CreateResponseAsync(followUp, ct);
    currentResponse = nextResponse.Value;
}
```

## Tasks To Complete

### 1. Fix AgentManager.cs build error (ALREADY IDENTIFIED)
File: `src/DiscoveryAgent/Services/AgentManager.cs`

The `CreateAgentVersionAsync` returns `ClientResult<AgentVersion>`, not `AgentVersion` directly. Need to add `.Value` to unwrap:

```csharp
var agentVersionResult = await _projectClient.Agents.CreateAgentVersionAsync(
    agentName: _settings.AgentName,
    options: new(definition),
    cancellationToken: ct);

var agentVersion = agentVersionResult.Value;
```

Also ensure the file has `using Azure.AI.Projects.Agents;` for `PromptAgentDefinition`.

### 2. Revert debug error handler
File: `src/DiscoveryAgent/Functions/ConversationFunction.cs`

The catch block currently leaks `ex.Message`, `ex.GetType().Name`, and `ex.InnerException?.Message`. Change to:
```csharp
return new ObjectResult(new { error = "Internal error" }) { StatusCode = 500 };
```

### 3. Fix Bicep: switch AzureWebJobsStorage to managed identity
File: `infra/modules/function-app.bicep`

Replace line 37:
```bicep
{ name: 'AzureWebJobsStorage', value: 'DefaultEndpointsProtocol=https;AccountName=${funcStorage.name};EndpointSuffix=core.windows.net' }
```
With:
```bicep
{ name: 'AzureWebJobsStorage__accountName', value: funcStorage.name }
```

This resolves a GitGuardian false-positive alert on the connection string template AND is the correct MI-based pattern for Functions.

### 4. Add RBAC for MI-based storage
File: `infra/modules/role-assignments.bicep`

Add two new role definition variables:
```bicep
var storageBlobDataOwner = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var storageQueueDataContrib = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
```

Add two new role assignments for the function app's managed identity:
```bicep
// Required for MI-based AzureWebJobsStorage__accountName (Functions host blob leases)
resource funcStorageBlobOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccountId, functionAppPrincipalId, storageBlobDataOwner)
  scope: resourceGroup()
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwner), principalId: functionAppPrincipalId, principalType: 'ServicePrincipal' }
}

// Required for MI-based AzureWebJobsStorage__accountName (Functions host queue triggers)
resource funcStorageQueue 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccountId, functionAppPrincipalId, storageQueueDataContrib)
  scope: resourceGroup()
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContrib), principalId: functionAppPrincipalId, principalType: 'ServicePrincipal' }
}
```

### 5. Verify build passes
After all changes, run `dotnet build` and fix any remaining errors. The beta SDK types shift between versions — if you hit a CS0246 (type not found) or CS1061 (member not found), check the actual type by inspecting the NuGet package DLL rather than guessing.

## Next Steps (build is clean, all committed)

### 6. Test live agent creation
Run the function app locally and verify the agent initializes against your Foundry project:

```bash
cd src/DiscoveryAgent
func start
```

Watch the startup logs for:
- `Agent version created: Name=..., Version=..., Id=...` → agent definition registered in Foundry
- Any `Foundry API error` → check PROJECT_ENDPOINT, model deployment name, and RBAC

If running locally, ensure these env vars or user secrets are set:
- `PROJECT_ENDPOINT` — your Foundry project endpoint (format: `https://<resource>.services.ai.azure.com/api/projects/<project>`)
- `MODEL_DEPLOYMENT_NAME` — the model deployment in your Foundry project (e.g., `gpt-4.1`)
- `AGENT_NAME` — the agent name to register (e.g., `discovery-bot`)
- `COSMOS_ENDPOINT`, `COSMOS_DATABASE` — your BYO Cosmos
- `STORAGE_ENDPOINT`, `AI_SEARCH_ENDPOINT`, `KNOWLEDGE_INDEX_NAME`

### 7. Test a conversation end-to-end
Once the agent initializes, send a test request:

```bash
curl -X POST http://localhost:7071/api/conversation \
  -H "Content-Type: application/json" \
  -d '{"userId": "test-user", "message": "Hi, I am a project manager responsible for cloud migration."}'
```

Expected behavior:
1. A new Foundry conversation is created (check logs for `Created conversation: ...`)
2. The agent responds with discovery questions about the user's role
3. The agent calls `store_user_profile` tool (check logs for `Processing tool call: store_user_profile`)
4. The agent calls `extract_knowledge` tool to capture facts from the user's message
5. The response includes `extractedKnowledgeIds` in the JSON output

If tool calls don't fire, the agent definition may not have the tools attached. Check the Foundry portal to verify the agent version shows three function tools.

### 8. Test conversation continuity
Send a follow-up using the conversation ID from the first response:

```bash
curl -X POST http://localhost:7071/api/conversation \
  -H "Content-Type: application/json" \
  -d '{"userId": "test-user", "conversationId": "<id-from-step-7>", "message": "Our biggest challenge is migrating legacy databases without downtime."}'
```

Verify:
- Same conversation is resumed (logs show `Resuming conversation: ...`)
- `extract_knowledge` fires with items categorized as `requirement` or `concern`
- Knowledge items are written to Cosmos `knowledge-items` container

### 9. Test with a discovery context
Create a context in Cosmos `discovery-sessions` container, then:

```bash
curl -X POST http://localhost:7071/api/conversation \
  -H "Content-Type: application/json" \
  -d '{"userId": "test-user", "contextId": "your-context-id", "message": "Let us get started."}'
```

The agent should adapt its questions to the discovery areas and key questions defined in the context.

### 10. Deploy to Azure
Once local testing passes:

```bash
cd src/DiscoveryAgent
dotnet publish -c Release -o ./publish
cd publish
zip -r ../publish.zip .
cd ..
az functionapp deployment source config-zip \
  --name discdev-func-3xr5ve \
  --resource-group discovery-dev \
  --src publish.zip
```

Then set the app settings on the function app if not already configured:
```bash
az functionapp config appsettings set \
  --name discdev-func-3xr5ve \
  --resource-group discovery-dev \
  --settings \
    PROJECT_ENDPOINT="https://..." \
    MODEL_DEPLOYMENT_NAME="gpt-4.1" \
    AGENT_NAME="discovery-bot" \
    COSMOS_ENDPOINT="https://discdev-cosmos-3xr5ve.documents.azure.com:443/" \
    COSMOS_DATABASE="discovery" \
    STORAGE_ENDPOINT="https://discdevstor3xr5ve.blob.core.windows.net" \
    AI_SEARCH_ENDPOINT="https://discdev-search-3xr5ve.search.windows.net" \
    KNOWLEDGE_INDEX_NAME="knowledge-items"
```

### 11. Infra fixes (if not already applied)
Apply the Bicep changes from tasks 3 and 4 above, then redeploy infrastructure:
```bash
az deployment group create \
  --resource-group discovery-dev \
  --template-file infra/main.bicep \
  --parameters infra/params/dev.bicepparam
```

### Debugging Tips
- If `CreateAgentVersionAsync` returns 404: the Foundry project endpoint is wrong or the project doesn't exist
- If `CreateAgentVersionAsync` returns 403: the managed identity (or your az login identity) needs `Azure AI User` role on the Foundry project
- If tool calls never appear in the response: check that the agent version in the Foundry portal shows the three function tools. If not, the `PromptAgentDefinition.Tools` collection may not be serializing correctly — add a log line to dump `definition.Tools.Count` before the create call
- If conversation creation fails with 400: the BYO Cosmos capability host may not be configured. Check the Foundry portal → Project settings → Capability host
- If knowledge items aren't written to Cosmos: check the `KnowledgeStore` service registration and that the Cosmos `discovery` database + `knowledge-items` container exist

## Files Changed Summary
| File | Change |
|------|--------|
| `src/DiscoveryAgent/Services/AgentManager.cs` | Unwrap `ClientResult<AgentVersion>` with `.Value`, ensure `using Azure.AI.Projects.Agents;` |
| `src/DiscoveryAgent/Functions/ConversationFunction.cs` | Revert error handler to `"Internal error"` |
| `infra/modules/function-app.bicep` | MI-based storage (`AzureWebJobsStorage__accountName`) |
| `infra/modules/role-assignments.bicep` | Add Storage Blob Data Owner + Storage Queue Data Contributor RBAC |
