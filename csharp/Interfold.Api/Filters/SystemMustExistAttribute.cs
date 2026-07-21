using System.Net;
using Interfold.Api.Models;
using Interfold.Contracts;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions.Repository;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Interfold.Api.Filters;

/// <summary>
/// Action filter that hydrates the <c>systemId</c> route value into a
/// <see cref="SystemId"/>, looks the account up via
/// <see cref="IAccountRepository.GetPublicProfileAsync"/>, and short-circuits the
/// pipeline with a 404 <see cref="ErrorResponse"/> (code
/// <see cref="ErrorCodes.SystemNotFound"/>) when the account is unknown. Consolidates
/// the identical 5-line <c>if (!await SystemExistsAsync(systemId, ct)) return
/// NotFound;</c> prelude that the read-only <c>PublicSystemsController</c> actions
/// repeat.
/// </summary>
/// <remarks>
/// Applied only to actions that treat the systemId as an existence gate — so
/// <see cref="Interfold.Api.Controllers.PublicSystemsController.Show"/> is
/// deliberately unadorned because its own <c>GetPublicProfileAsync</c> call IS the
/// lookup that produces the response body; wearing this filter would double the
/// account round-trip.
/// <para>
/// The filter reads the systemId from the route rather than model-binding a
/// <see cref="SystemId"/> parameter of its own because ASP.NET Core route binding
/// happens BEFORE action filters run — the parsed parameter is already available on
/// <see cref="ActionExecutingContext.ActionArguments"/> and the model binder has
/// already validated the shape. We fall back to the route dictionary for the case
/// where the parameter name diverges from the route template's placeholder.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SystemMustExistAttribute : Attribute, IAsyncActionFilter
{
    // Kept as a constant so a route-template rename surfaces as a compile-time issue
    // (via the [Route] on the controller) rather than a silent 404 at request time.
    private const string SystemIdRouteKey = "systemId";

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var systemId = ExtractSystemId(context);
        if (systemId is null)
        {
            // Missing / blank route value can't be an account we know about; a defensive
            // 404 matches what the hand-rolled prelude used to return.
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
        // Prefer the already-bound argument (the model binder produced a SystemId with the
        // shape validated) over re-parsing the route string. Falls back to the route
        // dictionary for actions that don't declare a `SystemId systemId` parameter.
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
