using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Interfold.Api.Services.OAuth;
using Interfold.Contracts.Configuration;
using Microsoft.Extensions.Options;
using Interfold.Contracts.Ids;
using Interfold.Api.Auth;

namespace Interfold.Api.Services;

/// <summary>
/// Handles Apple OAuth2 backend token exchange and identity extraction.
/// </summary>
public sealed class AppleOAuthService
{
    private const string TokenEndpoint = "https://appleid.apple.com/auth/token";

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<AuthenticationConfiguration> _authOptions;

    public AppleOAuthService(HttpClient httpClient, IOptionsMonitor<AuthenticationConfiguration> authOptions)
    {
        _httpClient = httpClient;
        _authOptions = authOptions;
    }

    /// <summary>
    /// Exchanges an Apple authorization code and returns the stable Apple user identifier (sub).
    /// Returns null if configuration is incomplete or exchange fails.
    /// </summary>
    public async Task<AppleId?> ExchangeCodeForAppleIdAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        var authConfig = _authOptions.CurrentValue;
        if (string.IsNullOrWhiteSpace(authConfig.AppleOAuthClientId) ||
            string.IsNullOrWhiteSpace(authConfig.AppleOAuthClientSecret))
        {
            return null;
        }

        try
        {
            var tokenRequest = new Dictionary<string, string>
            {
                { OAuthQueryKeys.GrantType, OAuthQueryKeys.AuthorizationCodeGrant },
                { OAuthQueryKeys.Code, code },
                { OAuthQueryKeys.RedirectUri, redirectUri },
                { OAuthQueryKeys.ClientId, authConfig.AppleOAuthClientId },
                { OAuthQueryKeys.ClientSecret, authConfig.AppleOAuthClientSecret }
            };

            using var content = new FormUrlEncodedContent(tokenRequest);
            using var tokenResponse = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var tokenPayload = await tokenResponse.Content.ReadFromJsonAsync<OAuthTokenResponse>(cancellationToken);
            return ExtractSubFromJwt(tokenPayload?.IdToken);
        }
        catch
        {
            return null;
        }
    }

    public AppleId? ExtractSubFromJwt(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return null;
        }

        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payloadBytes = Base64UrlDecode(parts[1]);
            var payload = JsonSerializer.Deserialize<AppleIdTokenPayload>(payloadBytes);
            return string.IsNullOrWhiteSpace(payload?.Sub) ? null : new AppleId(payload.Sub);
        }
        catch
        {
            return null;
        }
    }

    private sealed record AppleIdTokenPayload
    {
        [JsonPropertyName("sub")]
        public string? Sub { get; init; }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var normalized = input
            .Replace('-', '+')
            .Replace('_', '/');

        var padding = 4 - (normalized.Length % 4);
        if (padding is > 0 and < 4)
        {
            normalized = normalized + new string('=', padding);
        }

        return Convert.FromBase64String(normalized);
    }
}
