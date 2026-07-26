using System.Text.Json.Serialization;

namespace Interfold.Settings.Contracts.Models.Wire;

/// <summary>Response body for <c>GET /api/settings/firebase-config?platform=…</c>.
/// The three variants map 1:1 to the client-side Firebase init shapes; STJ emits a
/// <c>type</c> discriminator ("android"/"ios"/"web") matching the query parameter so
/// the client's kotlinx-serialization sealed hierarchy dispatches without a wrapper.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FirebaseAndroidConfigResponse), typeDiscriminator: "android")]
[JsonDerivedType(typeof(FirebaseIosConfigResponse), typeDiscriminator: "ios")]
[JsonDerivedType(typeof(FirebaseWebConfigResponse), typeDiscriminator: "web")]
public abstract record FirebaseClientConfigResponse;

/// <summary>
/// Android response variant. Fields map into
/// <c>com.google.firebase.FirebaseOptions.Builder</c> on the client.
/// </summary>
public sealed record FirebaseAndroidConfigResponse(
    string ApiKey,
    string ApplicationId,
    string ProjectId,
    string GcmSenderId,
    string? StorageBucket) : FirebaseClientConfigResponse;

/// <summary>
/// iOS response variant. Fields map into <c>FIROptions</c> / <c>FirebaseOptions</c> on
/// the client. <c>ClientId</c> is optional because it's only meaningful for deployments
/// that use Firebase Auth's Google Sign-In (FCM-only setups can leave it null).
/// </summary>
public sealed record FirebaseIosConfigResponse(
    string ApiKey,
    string GoogleAppId,
    string GcmSenderId,
    string ProjectId,
    string? StorageBucket,
    string BundleId,
    string? ClientId) : FirebaseClientConfigResponse;

/// <summary>
/// Web (wasm) response variant. Includes the public VAPID key required by
/// <c>messaging.getToken({ vapidKey })</c> in the Firebase JS SDK.
/// </summary>
public sealed record FirebaseWebConfigResponse(
    string ApiKey,
    string AuthDomain,
    string ProjectId,
    string? StorageBucket,
    string MessagingSenderId,
    string AppId,
    string VapidKey) : FirebaseClientConfigResponse;
