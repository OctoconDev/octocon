namespace Interfold.Contracts.Configuration;

/// <summary>Per-platform Firebase client-init payloads served by
/// <c>GET /api/settings/firebase-config?platform=…</c>. Public values — safe over an
/// unauth endpoint. Populated at startup from three optional <c>internal.secrets</c> rows
/// (<c>firebase:client:{android|ios|web}</c>); a missing row leaves the property null and
/// the endpoint returns 503 for that platform.</summary>
public sealed class FirebaseClientConfiguration
{
    public const string SectionName = "Octocon:Firebase";

    public FirebaseAndroidClientConfig? Android { get; set; }

    public FirebaseIosClientConfig? Ios { get; set; }

    public FirebaseWebClientConfig? Web { get; set; }
}

/// <summary>Android Firebase client-init payload (mirrors <c>FirebaseOptions.Builder</c>).</summary>
public sealed record FirebaseAndroidClientConfig(
    string ApiKey,
    string ApplicationId,
    string ProjectId,
    string GcmSenderId,
    string? StorageBucket);

/// <summary>iOS Firebase client-init payload (mirrors <c>FirebaseOptions</c>).</summary>
public sealed record FirebaseIosClientConfig(
    string ApiKey,
    string GoogleAppId,
    string GcmSenderId,
    string ProjectId,
    string? StorageBucket,
    string BundleId,
    string? ClientId);

/// <summary>Web (wasm) Firebase client-init payload. VapidKey is the public Web Push key
/// passed to <c>messaging.getToken({ vapidKey })</c>.</summary>
public sealed record FirebaseWebClientConfig(
    string ApiKey,
    string AuthDomain,
    string ProjectId,
    string? StorageBucket,
    string MessagingSenderId,
    string AppId,
    string VapidKey);
