using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Api.Models;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Microsoft.AspNetCore.Http;
using Interfold.Contracts;

namespace Interfold.Api.Helpers;

/// <summary>
/// Pure-function bridge between <see cref="FirebaseClientConfiguration"/> and the
/// polymorphic <see cref="FirebaseClientConfigResponse"/> shape the client consumes.
/// Kept here (rather than inlined into <c>SettingsController</c>) so the shape mapping
/// and the cache-header contract are directly unit-testable without spinning up a
/// controller / TestServer.
/// </summary>
public static class FirebaseConfigResolver
{
    /// <summary>
    /// Snake-case serializer options used exclusively for the firebase-config ETag hash.
    /// Kept internal so a request-time serialization allocation isn't paid for each hit;
    /// the options mirror the global controller default so the hash is stable against the
    /// wire payload's actual byte shape.
    /// </summary>
    internal static readonly JsonSerializerOptions EtagJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    /// <summary>
    /// Resolves the requested <paramref name="platform"/> against
    /// <paramref name="configuration"/>. Returns either a populated payload (with a null
    /// error) or a shaped <see cref="ErrorResponse"/> (with a null payload) — never both
    /// null and never both set. The three error shapes:
    /// <list type="bullet">
    ///   <item><c>invalid_platform</c> / 400 — <paramref name="platform"/> is null, empty,
    ///         or not one of <c>android|ios|web</c> (case-insensitive).</item>
    ///   <item><c>firebase_config_unavailable</c> / 503 — the platform is valid but the
    ///         matching <c>internal.secrets:firebase:client:*</c> row is absent, so the
    ///         deployment hasn't wired that platform's Firebase project.</item>
    /// </list>
    /// </summary>
    public static (FirebaseClientConfigResponse? Payload, ErrorResponse? Error) Resolve(
        FirebaseClientConfiguration configuration,
        string? platform)
    {
        // The query value stays a raw string so an unknown platform produces the legacy
        // invalid_platform 400 body rather than a model-binding failure.
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

    /// <summary>
    /// Stamps <c>Cache-Control</c> and a body-hash <c>ETag</c> on the current response
    /// for the firebase-config endpoint. Rotations here are more time-sensitive than the
    /// public key: the wasm service worker re-fetches on <c>install</c> and needs to see
    /// the new config within a bounded window after a Firebase Console rotation. Five
    /// minutes is a defensible ceiling — long enough that a viral service-worker refresh
    /// doesn't hammer the API, short enough that a rotation is picked up before the next
    /// nightly deploy.
    /// <para>
    /// The ETag is derived from a stable snake-case JSON serialisation of the payload so
    /// identical payloads always produce identical ETags across processes.
    /// </para>
    /// </summary>
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
