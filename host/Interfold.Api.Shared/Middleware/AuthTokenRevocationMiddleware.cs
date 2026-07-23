using System.Text.Json;
using Interfold.Api.Models;
using Interfold.Contracts;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Api.Middleware;

/// <summary>Post-authentication JTI-revocation gate. Rejects authenticated requests
/// whose JWT <c>jti</c> claim has been revoked (logout, incident response, or
/// background expiry cleanup) with a 401 carrying <see cref="ErrorCodes.TokenRevoked"/>.
/// Unauthenticated requests and authenticated-without-jti requests fall through
/// untouched — matches the inline shape this replaced in Program.cs.</summary>
public sealed class AuthTokenRevocationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            if (context.User.FindFirst(JwtClaimNames.Jti)?.Value is { } jti && !string.IsNullOrWhiteSpace(jti))
            {
                var revocationRepository = context.RequestServices.GetRequiredService<IAuthTokenRevocationRepository>();
                var isTokenValid = await revocationRepository.ValidateTokenNotRevokedAsync(new Jti(jti), context.RequestAborted);

                if (!isTokenValid)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    var error = new ErrorResponse("Token has been revoked.", ErrorCodes.TokenRevoked);
                    var json = JsonSerializer.Serialize(error, JsonSerializerOptions.Web);
                    await context.Response.WriteAsync(json, context.RequestAborted);
                    return;
                }
            }
        }

        await next(context);
    }
}
