using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Api.Models;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Microsoft.AspNetCore.Http;
using Interfold.Shared.Contracts;

namespace Interfold.Api.Helpers;

/// <summary>Pure-function bridge from <see cref="FirebaseClientConfiguration"/> to the
/// polymorphic <see cref="FirebaseClientConfigResponse"/> shape. Extracted from
/// <c>SettingsController</c> so mapping + cache-header logic is unit-testable without a
/// TestServer.</summary>
public static class FirebaseConfigResolver
{
    // Mirrors the controller's snake-case options so the ETag hashes the same bytes the
    // client actually receives.
    internal static readonly JsonSerializerOptions EtagJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    /// <summary>Returns either a populated payload OR a shaped <see cref="ErrorResponse"/>
    /// (never both, never both null). Errors: <c>invalid_platform</c>/400,
    /// <c>firebase_config_unavailable</c>/503.</summary>
    public static (FirebaseClientConfigResponse? Payload, ErrorResponse? Error) Resolve(
        FirebaseClientConfiguration configuration,
        string? platform)
    {
        // Raw string so an unknown platform still yields the legacy invalid_platform 400.
        if (!platform.TryParseWire<ClientPlatform>(out var parsed))
        {
            return (null, new ErrorResponse(
                "Invalid platform. Expected one of: android, ios, web.",
                ErrorCodes.InvalidPlatform,
                HttpStatusCode.BadRequest));
        }

        switch (parsed)
        {
            case ClientPlatform.Android:
                var android = configuration.Android;
                if (android is null) return (null, Unavailable());
                return (new FirebaseAndroidConfigResponse(
                    android.ApiKey, android.ApplicationId, android.ProjectId,
                    android.GcmSenderId, android.StorageBucket), null);

            case ClientPlatform.Ios:
                var ios = configuration.Ios;
                if (ios is null) return (null, Unavailable());
                return (new FirebaseIosConfigResponse(
                    ios.ApiKey, ios.GoogleAppId, ios.GcmSenderId, ios.ProjectId,
                    ios.StorageBucket, ios.BundleId, ios.ClientId), null);

            case ClientPlatform.Web:
                var web = configuration.Web;
                if (web is null) return (null, Unavailable());
                return (new FirebaseWebConfigResponse(
                    web.ApiKey, web.AuthDomain, web.ProjectId, web.StorageBucket,
                    web.MessagingSenderId, web.AppId, web.VapidKey), null);

            default:
                throw new InvalidOperationException(
                    $"Unhandled {nameof(ClientPlatform)} value '{parsed}' after wire parse.");
        }
    }

    /// <summary>Stamps <c>Cache-Control: max-age=300</c> and a snake-case-JSON body-hash
    /// ETag. 5min is the ceiling for wasm service-worker rotation pickup.</summary>
    public static void ApplyCacheHeaders(HttpResponse response, FirebaseClientConfigResponse payload)
    {
        response.Headers["Cache-Control"] = "public, max-age=300, must-revalidate";

        var canonical = JsonSerializer.Serialize<FirebaseClientConfigResponse>(payload, EtagJsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var hex = Convert.ToHexString(hash);
        response.Headers.ETag = $"\"{hex}\"";
    }

    private static ErrorResponse Unavailable() => new(
        "Firebase config is not available for the requested platform.",
        ErrorCodes.FirebaseConfigUnavailable,
        HttpStatusCode.ServiceUnavailable);
}
