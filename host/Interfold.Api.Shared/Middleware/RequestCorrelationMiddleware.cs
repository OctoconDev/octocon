using System.Diagnostics;
using Microsoft.Extensions.Primitives;
using Interfold.Contracts;

namespace Interfold.Api.Middleware;

/// <summary>
/// Middleware that:
/// <list type="bullet">
///   <item>Reads (or generates) a <c>X-Interfold-Request-Id</c> correlation ID from the incoming request.</item>
///   <item>Pushes <c>RequestId</c>, <c>RequestPath</c>, and <c>RequestMethod</c> into the log scope.</item>
///   <item>Echoes the <c>X-Interfold-Request-Id</c> header back on every response.</item>
///   <item>Logs the completed request at <c>Information</c> level with <c>StatusCode</c> and <c>ElapsedMs</c>.</item>
/// </list>
/// </summary>
public sealed class RequestCorrelationMiddleware(RequestDelegate next, ILogger<RequestCorrelationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = GetOrGenerateRequestId(context);

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[InterfoldHeaders.RequestId] = requestId;
            return Task.CompletedTask;
        });

        var sw = Stopwatch.StartNew();

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["RequestId"]     = requestId,
            ["RequestPath"]   = context.Request.Path.ToString(),
            ["RequestMethod"] = context.Request.Method
        }))

        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();
            logger.LogInformation(
                "HTTP {Method} {Path} → {StatusCode} in {ElapsedMs:F1} ms",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                sw.Elapsed.TotalMilliseconds);
        }
    }

    private static string GetOrGenerateRequestId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(InterfoldHeaders.InboundRequestId, out StringValues existing)
            && !StringValues.IsNullOrEmpty(existing))
        {
            return existing.ToString();
        }

        return Guid.NewGuid().ToString("N");
    }
}
