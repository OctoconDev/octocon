using Interfold.Shared.Contracts.Configuration;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Interfold.Api.Swagger;

public class HealthCheckDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        var healthyResponse = new OpenApiResponse { Description = "Healthy" };
        var unhealthyResponse = new OpenApiResponse { Description = "Unhealthy" };

        swaggerDoc.Paths[HealthEndpoints.Live] = new OpenApiPathItem
        {
            Operations =
            {
                [OperationType.Get] = new OpenApiOperation
                {
                    Tags = [new OpenApiTag { Name = "Health" }],
                    Summary = "Liveness probe",
                    Description = "Returns 200 if the process is running. No dependency checks.",
                    Responses = new OpenApiResponses
                    {
                        ["200"] = healthyResponse
                    }
                }
            }
        };

        swaggerDoc.Paths[HealthEndpoints.Ready] = new OpenApiPathItem
        {
            Operations =
            {
                [OperationType.Get] = new OpenApiOperation
                {
                    Tags = [new OpenApiTag { Name = "Health" }],
                    Summary = "Readiness probe",
                    Description = "Returns 200 if all dependencies (Scylla, Postgres) are reachable.",
                    Responses = new OpenApiResponses
                    {
                        ["200"] = healthyResponse,
                        ["503"] = unhealthyResponse
                    }
                }
            }
        };

        swaggerDoc.Paths[HealthEndpoints.Startup] = new OpenApiPathItem
        {
            Operations =
            {
                [OperationType.Get] = new OpenApiOperation
                {
                    Tags = [new OpenApiTag { Name = "Health" }],
                    Summary = "Startup probe",
                    Description = "Returns 200 once all dependencies are initialized. Uses longer timeouts than readiness.",
                    Responses = new OpenApiResponses
                    {
                        ["200"] = healthyResponse,
                        ["503"] = unhealthyResponse
                    }
                }
            }
        };
    }
}
