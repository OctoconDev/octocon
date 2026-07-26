using System.Net;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Interfold.Systems.Api.Filters;

/// <summary>Action filter that short-circuits with 404
/// (<see cref="ErrorCodes.SystemNotFound"/>) when the <c>systemId</c> route value doesn't
/// resolve via <see cref="IAccountRepository.GetPublicProfileAsync"/>. Consolidates the
/// repeated existence-gate prelude on <c>PublicSystemsController</c> read actions; do
/// NOT apply where the endpoint already looks the account up to build its response body
/// (would double the round-trip).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SystemMustExistAttribute : Attribute, IAsyncActionFilter
{
    // Constant so a route-template rename surfaces as compile-time breakage on the [Route]
    // rather than a silent 404 at request time.
    private const string SystemIdRouteKey = "systemId";

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var systemId = ExtractSystemId(context);
        if (systemId is null)
        {
            context.Result = NotFoundResult();
            return;
        }

        var accounts = context.HttpContext.RequestServices.GetRequiredService<IAccountRepository>();
        var profile = await accounts.GetPublicProfileAsync(systemId.Value, context.HttpContext.RequestAborted);
        if (profile is null)
        {
            context.Result = NotFoundResult();
            return;
        }

        await next();
    }

    private static SystemId? ExtractSystemId(ActionExecutingContext context)
    {
        // Prefer the already-bound (shape-validated) argument; fall back to the raw route.
        if (context.ActionArguments.TryGetValue(SystemIdRouteKey, out var arg) && arg is SystemId bound)
        {
            return bound;
        }

        if (context.RouteData.Values.TryGetValue(SystemIdRouteKey, out var raw)
            && raw is string s
            && !string.IsNullOrWhiteSpace(s))
        {
            return new SystemId(s);
        }

        return null;
    }

    private static IActionResult NotFoundResult() => new ObjectResult(new ErrorResponse(
        "System not found.",
        ErrorCodes.SystemNotFound,
        HttpStatusCode.NotFound))
    {
        DeclaredType = typeof(ErrorResponse),
        StatusCode = (int)HttpStatusCode.NotFound,
    };
}
