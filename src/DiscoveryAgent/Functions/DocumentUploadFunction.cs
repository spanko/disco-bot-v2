using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DiscoveryAgent.Functions;

public class DocumentUploadFunction
{
    private readonly BlobServiceClient _blobService;
    private readonly ILogger<DocumentUploadFunction> _logger;

    private const string ContainerName = "documents";
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/msword",
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", "image/tiff",
    };

    public DocumentUploadFunction(BlobServiceClient blobService, ILogger<DocumentUploadFunction> logger)
    {
        _blobService = blobService;
        _logger = logger;
    }

    [Function("UploadDocument")]
    public async Task<IActionResult> Upload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "documents/upload")] HttpRequest req)
    {
        if (!req.HasFormContentType || req.Form.Files.Count == 0)
            return new BadRequestObjectResult(new { error = "No file uploaded. Use multipart/form-data." });

        var file = req.Form.Files[0];
        if (file.Length == 0)
            return new BadRequestObjectResult(new { error = "File is empty." });

        if (file.Length > 20 * 1024 * 1024)
            return new BadRequestObjectResult(new { error = "File too large. Max 20 MB." });

        var contentType = file.ContentType?.ToLowerInvariant() ?? "";
        if (!AllowedContentTypes.Contains(contentType))
            return new BadRequestObjectResult(new
            {
                error = $"Unsupported file type: {contentType}",
                allowed = AllowedContentTypes
            });

        var userId = req.Form["userId"].ToString();
        if (string.IsNullOrEmpty(userId)) userId = "anonymous";

        var contextId = req.Form["contextId"].ToString();
        if (string.IsNullOrEmpty(contextId)) contextId = "default";

        var documentId = Guid.NewGuid().ToString();
        var ext = Path.GetExtension(file.FileName) ?? "";
        var blobName = $"{contextId}/{userId}/{documentId}{ext}";

        try
        {
            var container = _blobService.GetBlobContainerClient(ContainerName);
            await container.CreateIfNotExistsAsync();

            var blob = container.GetBlobClient(blobName);
            var headers = new BlobHttpHeaders { ContentType = contentType };

            using var stream = file.OpenReadStream();
            await blob.UploadAsync(stream, new BlobUploadOptions { HttpHeaders = headers });

            // Store metadata on the blob
            await blob.SetMetadataAsync(new Dictionary<string, string>
            {
                ["originalName"] = file.FileName,
                ["userId"] = userId,
                ["contextId"] = contextId,
                ["uploadedAt"] = DateTime.UtcNow.ToString("O"),
            });

            var isImage = contentType.StartsWith("image/");

            _logger.LogInformation(
                "Document uploaded: {DocumentId} ({FileName}, {Size} bytes, {Type}) by {UserId}",
                documentId, file.FileName, file.Length, contentType, userId);

            return new OkObjectResult(new
            {
                documentId,
                fileName = file.FileName,
                contentType,
                size = file.Length,
                blobUrl = blob.Uri.ToString(),
                isImage,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document upload failed: {FileName}", file.FileName);
            return new ObjectResult(new { error = "Upload failed" }) { StatusCode = 500 };
        }
    }

    [Function("GetDocumentContent")]
    public async Task<IActionResult> GetContent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "documents/{documentId}/content")] HttpRequest req,
        string documentId)
    {
        try
        {
            var container = _blobService.GetBlobContainerClient(ContainerName);

            // Search for the blob by documentId prefix across all paths
            await foreach (var blob in container.GetBlobsAsync())
            {
                if (blob.Name.Contains(documentId))
                {
                    var blobClient = container.GetBlobClient(blob.Name);
                    var props = await blobClient.GetPropertiesAsync();
                    var contentType = props.Value.ContentType;
                    var isImage = contentType.StartsWith("image/");

                    if (isImage)
                    {
                        // Return base64-encoded image for the agent
                        using var ms = new MemoryStream();
                        await blobClient.DownloadToAsync(ms);
                        var base64 = Convert.ToBase64String(ms.ToArray());
                        return new OkObjectResult(new
                        {
                            documentId,
                            contentType,
                            isImage = true,
                            base64Data = base64,
                            fileName = GetMeta(props.Value.Metadata, "originalName"),
                        });
                    }
                    else
                    {
                        // Return download URL for document types
                        return new OkObjectResult(new
                        {
                            documentId,
                            contentType,
                            isImage = false,
                            blobUrl = blobClient.Uri.ToString(),
                            fileName = GetMeta(props.Value.Metadata, "originalName"),
                        });
                    }
                }
            }

            return new NotFoundObjectResult(new { error = "Document not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get document: {DocumentId}", documentId);
            return new ObjectResult(new { error = "Failed to retrieve document" }) { StatusCode = 500 };
        }
    }

    private static string GetMeta(IDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) ? value : "";
}
