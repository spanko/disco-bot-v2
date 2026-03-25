# Authoritative C# Function Calling Sample
# Source: https://learn.microsoft.com/en-us/azure/ai-foundry/agents/how-to/tools/function-calling?view=foundry

This is the COMPLETE working example from the official Microsoft docs for function
calling with Foundry Agent Service (GA, Responses API). Use this as the reference
pattern for the disco-bot-v2 ConversationHandler tool call loop.

## Key Pattern Notes

1. `ResponsesClient` (not `ProjectResponsesClient`) is used via `GetProjectResponsesClientForAgent(agentVersion.Name)`
2. The tool call loop uses `CreateResponse(previousResponseId:, inputItems:)` — NOT `CreateResponseOptions`
3. Tools are added directly to `PromptAgentDefinition.Tools` as `FunctionTool` instances
4. The loop collects ALL output items + function call outputs into `inputItems`, then calls `CreateResponse` again
5. `previousResponseId` chains the responses together (NOT conversation binding)

## Complete C# Code

```csharp
class FunctionCallingDemo
{
    private static string GetUserFavoriteCity() => "Seattle, WA";

    private static string GetCityNickname(string location) => location switch
    {
        "Seattle, WA" => "The Emerald City",
        _ => throw new NotImplementedException(),
    };

    public static string GetWeatherAtLocation(string location, string temperatureUnit = "f") => location switch
    {
        "Seattle, WA" => temperatureUnit == "f" ? "70f" : "21c",
        _ => throw new NotImplementedException()
    };

    // Tool definitions — these go into PromptAgentDefinition.Tools
    public static readonly FunctionTool getUserFavoriteCityTool = ResponseTool.CreateFunctionTool(
        functionName: "getUserFavoriteCity",
        functionDescription: "Gets the user's favorite city.",
        functionParameters: BinaryData.FromString("{}"),
        strictModeEnabled: false
    );

    public static readonly FunctionTool getCityNicknameTool = ResponseTool.CreateFunctionTool(
        functionName: "getCityNickname",
        functionDescription: "Gets the nickname of a city, e.g. 'LA' for 'Los Angeles, CA'.",
        functionParameters: BinaryData.FromObjectAsJson(
            new
            {
                Type = "object",
                Properties = new
                {
                    Location = new
                    {
                        Type = "string",
                        Description = "The city and state, e.g. San Francisco, CA",
                    },
                },
                Required = new[] { "location" },
            },
            new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        ),
        strictModeEnabled: false
    );

    private static readonly FunctionTool getCurrentWeatherAtLocationTool = ResponseTool.CreateFunctionTool(
        functionName: "getCurrentWeatherAtLocation",
        functionDescription: "Gets the current weather at a provided location.",
        functionParameters: BinaryData.FromObjectAsJson(
             new
             {
                 Type = "object",
                 Properties = new
                 {
                     Location = new
                     {
                         Type = "string",
                         Description = "The city and state, e.g. San Francisco, CA",
                     },
                     Unit = new
                     {
                         Type = "string",
                         Enum = new[] { "c", "f" },
                     },
                 },
                 Required = new[] { "location" },
             },
            new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        ),
        strictModeEnabled: false
    );

    // Function dispatch — matches function name to local implementation
    private static FunctionCallOutputResponseItem GetResolvedToolOutput(FunctionCallResponseItem item)
    {
        if (item.FunctionName == getUserFavoriteCityTool.FunctionName)
        {
            return ResponseItem.CreateFunctionCallOutputItem(item.CallId, GetUserFavoriteCity());
        }
        using JsonDocument argumentsJson = JsonDocument.Parse(item.FunctionArguments);
        if (item.FunctionName == getCityNicknameTool.FunctionName)
        {
            string locationArgument = argumentsJson.RootElement.GetProperty("location").GetString();
            return ResponseItem.CreateFunctionCallOutputItem(item.CallId, GetCityNickname(locationArgument));
        }
        if (item.FunctionName == getCurrentWeatherAtLocationTool.FunctionName)
        {
            string locationArgument = argumentsJson.RootElement.GetProperty("location").GetString();
            if (argumentsJson.RootElement.TryGetProperty("unit", out JsonElement unitElement))
            {
                string unitArgument = unitElement.GetString();
                return ResponseItem.CreateFunctionCallOutputItem(item.CallId, GetWeatherAtLocation(locationArgument, unitArgument));
            }
            return ResponseItem.CreateFunctionCallOutputItem(item.CallId, GetWeatherAtLocation(locationArgument));
        }
        return null;
    }

    public static void Main()
    {
        var projectEndpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
        var modelDeploymentName = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT_NAME");
        AIProjectClient projectClient = new(endpoint: new Uri(projectEndpoint), tokenProvider: new DefaultAzureCredential());

        // 1. Create agent with tools
        PromptAgentDefinition agentDefinition = new(model: modelDeploymentName)
        {
            Instructions = "You are a weather bot. Use the provided functions to help answer questions. "
                    + "Customize your responses to the user's preferences as much as possible and use friendly "
                    + "nicknames for cities whenever possible.",
            Tools = { getUserFavoriteCityTool, getCityNicknameTool, getCurrentWeatherAtLocationTool }
        };
        AgentVersion agentVersion = projectClient.Agents.CreateAgentVersion(
            agentName: "myAgent",
            options: new(agentDefinition));

        // 2. Get response client for the agent
        ResponsesClient responseClient = projectClient.OpenAI.GetProjectResponsesClientForAgent(agentVersion.Name);

        // 3. THE TOOL CALL LOOP — this is the critical pattern
        ResponseItem request = ResponseItem.CreateUserMessageItem("What's the weather like in my favorite city?");
        var inputItems = new List<ResponseItem> { request };
        string previousResponseId = null;
        bool functionCalled = false;
        ResponseResult response;
        do
        {
            // Call CreateResponse with previousResponseId to chain responses
            response = responseClient.CreateResponse(
                previousResponseId: previousResponseId,
                inputItems: inputItems);

            previousResponseId = response.Id;
            inputItems.Clear();
            functionCalled = false;

            // Process output items — add ALL items back to inputItems,
            // plus function call outputs for any tool calls
            foreach (ResponseItem responseItem in response.OutputItems)
            {
                inputItems.Add(responseItem);
                if (responseItem is FunctionCallResponseItem functionToolCall)
                {
                    Console.WriteLine($"Calling {functionToolCall.FunctionName}...");
                    inputItems.Add(GetResolvedToolOutput(functionToolCall));
                    functionCalled = true;
                }
            }
        } while (functionCalled);

        Console.WriteLine(response.GetOutputText());

        // Cleanup
        projectClient.Agents.DeleteAgentVersion(agentName: agentVersion.Name, agentVersion: agentVersion.Version);
    }
}
```

## Differences from disco-bot-v2 ConversationHandler

The disco-bot uses conversations (for multi-turn persistence), so the pattern is slightly
different. The key adaptation:

1. Create conversation + bind at client level: `GetProjectResponsesClientForAgent(agentName, conversation)`
2. First response: `CreateResponse("user message text")` or `CreateResponseAsync(CreateResponseOptions with InputItems)`
3. Tool call follow-up: use `previousResponseId` + `inputItems` (output items + function results)
4. Do NOT set both `conversation` and `previousResponseId` on the same call — they conflict

## Expected Output
```
Calling getUserFavoriteCity...
Calling getCityNickname...
Calling getCurrentWeatherAtLocation...
Your favorite city, Seattle, WA, is also known as The Emerald City. The current weather there is 70f.
```
