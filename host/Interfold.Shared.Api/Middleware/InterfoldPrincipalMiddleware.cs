using System.Security.Claims;
using Interfold.Api.Controllers.Base;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Api.Middleware;

/// <summary>
/// Resolves and validates principal IDs for Interfold API controllers.
/// </summary>
public sealed class InterfoldPrincipalMiddleware(RequestDelegate next)
{
    internal const string PrincipalIdItemKey = "Interfold.PrincipalId";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!RequiresInterfoldPrincipal(context))
        {
            await next(context);
            return;
        }

        var principalId = ResolvePrincipalId(context.User);
        if (principalId is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Items[PrincipalIdItemKey] = principalId;
        await next(context);
    }

    private static bool RequiresInterfoldPrincipal(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null)
            return false;

        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return false;

        var actionDescriptor = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        return actionDescriptor is not null
               && typeof(InterfoldControllerBase).IsAssignableFrom(actionDescriptor.ControllerTypeInfo.AsType());
    }

    /// <summary>
    /// The JWT <c>sub</c> claim is the one place a scoped-system-id string crosses the
    /// trust boundary into the process. TryParseScoped enforces the wire invariant here —
    /// a legacy or hand-crafted token that omits the region prefix (or names an unknown
    /// region) surfaces as a 401 rather than propagating an ambiguous <c>SystemId</c>
    /// deeper into the command pipeline. Every downstream site reads a
    /// <see cref="ScopedSystemId"/> from
    /// <c>HttpContext.Items[PrincipalIdItemKey]</c> and can rely on the type to prove it.
    /// </summary>
    private static ScopedSystemId? ResolvePrincipalId(ClaimsPrincipal user)
    {
        var sub = user.FindFirst(JwtClaimNames.Sub)?.Value
                  ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return ScopedSystemId.TryParseScoped(sub, out var scoped)
            ? scoped
            : null;
    }
}
