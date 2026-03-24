using System.Text.Json;
using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DiscoveryAgent.Functions;

public class ConversationFunction
{
    private readonly IConversationHandler _handler;
    private readonly ILogger<ConversationFunction> _logger;

    public ConversationFunction(IConversationHandler handler, ILogger<ConversationFunction> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    [Function("Conversation")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "conversation")] HttpRequest req)
    {
        try
        {
            var request = await JsonSerializer.DeserializeAsync<ConversationRequest>(
                req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (request is null || string.IsNullOrEmpty(request.Message))
                return new BadRequestObjectResult(new { error = "Message is required" });

            var response = await _handler.HandleAsync(request);
            return new OkObjectResult(response);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Agent not ready");
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 503 };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Conversation failed");
            return new ObjectResult(new { error = ex.Message, type = ex.GetType().Name, inner = ex.InnerException?.Message }) { StatusCode = 500 };
        }
    }
}
