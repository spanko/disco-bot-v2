# Answers to Claude Code SDK Questions

## Q1: "Model must match the agent's model" error

This error means the `CreateResponseOptions` is sending a model parameter that conflicts with the agent's model. When using `GetProjectResponsesClientForAgent`, the model comes FROM the agent definition — you should NOT set a model on `CreateResponseOptions`.

The issue is likely that `CreateResponseOptions(string, IEnumerable<ResponseItem>)` is the WRONG constructor. That first `string` parameter may be a model name, not a conversation ID. The Foundry quickstart shows a different pattern entirely:

```csharp
// Create conversation first
ProjectConversation conversation = projectClient.OpenAI.Conversations.CreateProjectConversation();

// Bind BOTH agent and conversation at the client level
ProjectResponsesClient responsesClient = projectClient.OpenAI.GetProjectResponsesClientForAgent(
    defaultAgent: agentName,
    defaultConversationId: conversation.Id);

// Then just call CreateResponse with the user message — no options needed
ResponseResult response = responsesClient.CreateResponse("What is the size of France?");

// Or async with options for follow-ups:
ResponseResult response = await responsesClient.CreateResponseAsync(
    userInputText: "Hello");
```

The conversation binding happens at client creation, not per-request.

## Q2: String overload vs AgentReference overload

Both work. The string overload resolves the agent by name (latest published version). The AgentReference overload lets you pin to a specific version. The model mismatch error is NOT caused by which overload you use — it's caused by the `CreateResponseOptions` constructor.

Check the overloads of `GetProjectResponsesClientForAgent`:
- `GetProjectResponsesClientForAgent(string defaultAgent)`
- `GetProjectResponsesClientForAgent(string defaultAgent, string defaultConversationId)` ← USE THIS ONE
- `GetProjectResponsesClientForAgent(AgentReference agentRef)`
- `GetProjectResponsesClientForAgent(AgentReference agentRef, ProjectConversation conversation)`

The two-parameter overload that takes both agent + conversation is the one from the quickstart.

## Q3: CreateResponseOptions constructor

The `CreateResponseOptions` class from `OpenAI.Responses` namespace has:
- `CreateResponseOptions()` — default, no model, no input
- `CreateResponseOptions(string model, IEnumerable<ResponseItem> inputItems)` — THIS IS THE ONE YOU'RE HITTING

The first string parameter is MODEL, not conversation ID. That's why you're getting "model must match" — you're passing a conversation ID as the model parameter.

For the Foundry agent pattern, you likely don't need `CreateResponseOptions` at all. Use:

```csharp
// Simple: just pass the text
ResponseResult response = await responsesClient.CreateResponseAsync("Hello");

// Or with ResponseItems:
ResponseResult response = await responsesClient.CreateResponseAsync(
    [ResponseItem.CreateUserMessageItem("Hello")]);

// For follow-ups with PreviousResponseId:
CreateResponseOptions options = new()
{
    PreviousResponseId = previousResponse.Id,
    InputItems = { ResponseItem.CreateUserMessageItem("Follow up question") },
};
ResponseResult followUp = await responsesClient.CreateResponseAsync(options);
```

## Q4: Complete end-to-end pattern

```csharp
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.AI.Extensions.OpenAI;
using Azure.Identity;
using OpenAI.Responses;

// 1. Create project client
var projectClient = new AIProjectClient(
    endpoint: new Uri("https://resource.services.ai.azure.com/api/projects/project"),
    tokenProvider: new DefaultAzureCredential());

// 2. Create/update agent version
var definition = new PromptAgentDefinition("gpt-4o")
{
    Instructions = "You are a helpful assistant.",
};
definition.Tools.Add(ResponseTool.CreateFunctionTool(
    functionName: "my_tool",
    functionDescription: "Does a thing",
    functionParameters: BinaryData.FromString("{}"),
    strictModeEnabled: false));

var result = await projectClient.Agents.CreateAgentVersionAsync(
    agentName: "my-agent",
    options: new(definition));
var agentVersion = result.Value;

// 3. Create a conversation
var conversation = await projectClient.OpenAI.Conversations
    .CreateProjectConversationAsync();

// 4. Get response client bound to agent + conversation
var responsesClient = projectClient.OpenAI.GetProjectResponsesClientForAgent(
    defaultAgent: agentVersion.Name,
    defaultConversationId: conversation.Value.Id);

// 5. Create response — NO model parameter, NO conversation ID in options
var response = await responsesClient.CreateResponseAsync("Hello!");
Console.WriteLine(response.Value.GetOutputText());

// 6. Follow-up (conversation continuity is automatic via the client binding)
var followUp = await responsesClient.CreateResponseAsync("Tell me more.");
Console.WriteLine(followUp.Value.GetOutputText());

// 7. For tool call loops, use PreviousResponseId:
var currentResponse = response.Value;
while (currentResponse.OutputItems.Any(i => i is FunctionCallResponseItem))
{
    var toolOutputs = new List<ResponseItem>();
    foreach (var item in currentResponse.OutputItems)
    {
        toolOutputs.Add(item);
        if (item is FunctionCallResponseItem fc)
        {
            toolOutputs.Add(ResponseItem.CreateFunctionCallOutputItem(
                fc.CallId, "{\"result\": \"done\"}"));
        }
    }

    CreateResponseOptions followUpOptions = new()
    {
        PreviousResponseId = currentResponse.Id,
    };
    foreach (var output in toolOutputs)
        followUpOptions.InputItems.Add(output);

    var next = await responsesClient.CreateResponseAsync(followUpOptions);
    currentResponse = next.Value;
}
```

## Q5: PROJECT_ENDPOINT format

Use the Foundry project endpoint format:
```
https://discdev-foundry-3xr5ve.services.ai.azure.com/api/projects/discdev-project
```

NOT the bare Cognitive Services endpoint (`https://disco-bot.cognitiveservices.azure.com/`).

The format is: `https://<resource-name>.services.ai.azure.com/api/projects/<project-name>`

You can find this in the Foundry portal → Project overview page. The `AIProjectClient` needs both the resource AND project in the URL because agents, conversations, and deployments are scoped to a project.

The `cognitiveservices.azure.com` endpoint is the old format (classic hub/project). The `services.ai.azure.com` endpoint is the new Foundry format that the 2.x SDK expects.

## Key Takeaway: Rewrite ConversationHandler

The root cause of the "model must match" error is using `CreateResponseOptions(conversationId, items)` where the first param is actually the MODEL, not the conversation ID. 

The fix: bind the conversation at client creation time using the two-parameter `GetProjectResponsesClientForAgent(agentName, conversationId)`, then just pass user messages directly without wrapping them in `CreateResponseOptions`.
