using System.Net.Http.Json;
using Interfold.Api.Services.OAuth;
using Interfold.Shared.Contracts.Configuration;
using Microsoft.Extensions.Options;
using Interfold.Shared.Contracts.Ids;
using Interfold.Api.Auth;

namespace Interfold.Api.Services;

/// <summary>
/// Handles Google OAuth2 backend token exchange and user info retrieval.
/// </summary>
public sealed class GoogleOAuthService
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v2/userinfo";

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<AuthenticationConfiguration> _authOptions;

    public GoogleOAuthService(HttpClient httpClient, IOptionsMonitor<AuthenticationConfiguration> authOptions)
    {
        _httpClient = httpClient;
        _authOptions = authOptions;
    }

    /// <summary>
    /// Exchange authorization code for email via Google's OAuth2 flow.
    /// Returns null if configuration is incomplete or exchange fails.
    /// </summary>
    public async Task<Email?> ExchangeCodeForEmailAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        var authConfig = _authOptions.CurrentValue;
        // Validate configuration
        if (string.IsNullOrWhiteSpace(authConfig.GoogleOAuthClientId) ||
            string.IsNullOrWhiteSpace(authConfig.GoogleOAuthClientSecret))
        {
            return null;
        }

        try
        {
            // Step 1: Exchange code for access token
            var tokenRequest = new Dictionary<string, string>
            {
                { OAuthQueryKeys.Code, code },
                { OAuthQueryKeys.ClientId, authConfig.GoogleOAuthClientId },
                { OAuthQueryKeys.ClientSecret, authConfig.GoogleOAuthClientSecret },
                { OAuthQueryKeys.GrantType, OAuthQueryKeys.AuthorizationCodeGrant },
                { OAuthQueryKeys.RedirectUri, redirectUri }
            };

            using var content = new FormUrlEncodedContent(tokenRequest);
            using var tokenResponse = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var tokenPayload = await tokenResponse.Content.ReadFromJsonAsync<OAuthTokenResponse>(cancellationToken);
            if (string.IsNullOrWhiteSpace(tokenPayload?.AccessToken))
            {
                return null;
            }

            // Step 2: Use access token to fetch user info
            using var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
            userInfoRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenPayload.AccessToken);

            using var userInfoResponse = await _httpClient.SendAsync(userInfoRequest, cancellationToken);

            if (!userInfoResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var userInfo = await userInfoResponse.Content.ReadFromJsonAsync<GoogleUserInfoResponse>(cancellationToken);
            return string.IsNullOrWhiteSpace(userInfo?.Email) ? null : new Email(userInfo.Email);
        }
        catch
        {
            // Log the error and return null to allow graceful degradation
            // In production, you'd want to log this properly
            return null;
        }
    }
}
