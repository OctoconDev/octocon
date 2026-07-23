using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Interfold.Api.Auth;

public sealed class RedirectChallengeOptions : AuthenticationSchemeOptions
{
    public string? AuthorizationEndpoint { get; set; }
    public Dictionary<string, string>? AdditionalParameters { get; set; }
}

public sealed class RedirectChallengeAuthenticationHandler : AuthenticationHandler<RedirectChallengeOptions>
{
    public RedirectChallengeAuthenticationHandler(
        IOptionsMonitor<RedirectChallengeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (string.IsNullOrWhiteSpace(Options.AuthorizationEndpoint))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        var location = Options.AuthorizationEndpoint!;
        var hasQueryString = location.Contains('?', StringComparison.Ordinal);
        
        if (Options.AdditionalParameters?.Count > 0)
        {
            foreach (var (key, value) in Options.AdditionalParameters)
            {
                var delimiter = hasQueryString ? "&" : "?";
                location = $"{location}{delimiter}{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
                hasQueryString = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(properties?.RedirectUri))
        {
            var delimiter = hasQueryString ? "&" : "?";
            location = $"{location}{delimiter}redirect_uri={Uri.EscapeDataString(properties.RedirectUri)}";
        }

        Response.Redirect(location);
        return Task.CompletedTask;
    }
}
