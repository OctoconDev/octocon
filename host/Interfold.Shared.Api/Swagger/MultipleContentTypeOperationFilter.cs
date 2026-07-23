using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Interfold.Api.Swagger;

public class MultipleContentTypeOperationFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        // Only allow conflicts for avatar upload endpoints
        var allowedConflicts = new[]
        {
            "api/settings/avatar",
            "api/systems/me/alters/{id}/avatar"
        };

        // Find avatar upload operations and ensure they support both JSON and multipart
        foreach (var path in swaggerDoc.Paths)
        {
            var routeLower = path.Key.ToLowerInvariant();
            if (!allowedConflicts.Any(routeLower.Contains))
                continue;

            foreach (var operation in path.Value.Operations.Values)
            {
                if (operation.RequestBody?.Content == null)
                    continue;

                // If this operation has JSON content, add multipart support
                var jsonContent = operation.RequestBody.Content
                    .FirstOrDefault(c => c.Key.StartsWith("application/json"));

                if (jsonContent.Value == null || operation.RequestBody.Content.ContainsKey("multipart/form-data"))
                {
                    continue;
                }

                operation.RequestBody.Content["multipart/form-data"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = "object",
                        Properties = new Dictionary<string, OpenApiSchema>
                        {
                            ["file"] = new OpenApiSchema
                            {
                                Type = "string",
                                Format = "binary",
                                Description = "The avatar file to upload"
                            },
                            ["idempotencyKey"] = new OpenApiSchema
                            {
                                Type = "string",
                                Nullable = true
                            }
                        }
                    }
                };

                // Update operation description
                if (!string.IsNullOrEmpty(operation.Description))
                {
                    operation.Description += "\n\nThis endpoint accepts both JSON (with avatarUrl) and multipart/form-data (with file upload).";
                }
                else
                {
                    operation.Description = "Accepts both JSON (with avatarUrl) and multipart/form-data (with file upload).";
                }
            }
        }
    }
}
