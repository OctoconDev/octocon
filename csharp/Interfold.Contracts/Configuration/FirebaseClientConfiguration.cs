namespace Interfold.Contracts.Configuration;

/// <summary>
/// Per-platform Firebase client-init payloads served by
/// <c>GET /api/settings/firebase-config?platform=…</c> and consumed by the mobile / wasm
/// apps at runtime. These are the values that previously lived inside every distributed
/// Android / iOS / Web bundle (<c>google-services.json</c>, <c>GoogleService-Info.plist</c>,
/// hardcoded web config) and are being centralised on the server so a Firebase project
/// rotation no longer requires a client re-release.
/// <para>
/// The three payloads are PUBLIC values — they authorise nothing on their own — so it is
/// safe to hand them out over an unauthenticated endpoint. The private half of the
/// Firebase story (the FCM v1 service-account credential used to <em>send</em>
/// notifications) lives separately under <c>fcm:service_account_json</c> in
/// <c>internal.secrets</c> and is never surfaced through any HTTP route.
/// </para>
/// <para>
/// Populated by <c>SecretsBootstrapService</c> at startup from three optional
/// <c>internal.secrets</c> rows (<c>firebase:client:android</c>,
/// <c>firebase:client:ios</c>, <c>firebase:client:web</c>). A missing row leaves the
/// matching property <c>null</c> and the endpoint returns 503 for that platform. Treat
/// as startup-only (matches how <see cref="AuthenticationConfiguration"/> handles its
/// secret-store-sourced fields) — there is no live-reload story yet.
/// </para>
/// </summary>
public sealed class FirebaseClientConfiguration
{
    public const string SectionName = "Octocon:Firebase";

    /// <summary>
    /// Android client-init payload. Sourced from <c>internal.secrets:firebase:client:android</c>
    /// (a JSON string parsed into <see cref="FirebaseAndroidClientConfig"/>). Null when the
    /// deployment hasn't seeded the row.
    /// </summary>
    public FirebaseAndroidClientConfig? Android { get; set; }

    /// <summary>
    /// iOS client-init payload. Sourced from <c>internal.secrets:firebase:client:ios</c>.
    /// Null when the deployment hasn't seeded the row.
    /// </summary>
    public FirebaseIosClientConfig? Ios { get; set; }

    /// <summary>
    /// Web (wasm) client-init payload. Includes the public VAPID key used by the FCM
    /// JS SDK's <c>getToken({ vapidKey })</c> call. Sourced from
    /// <c>internal.secrets:firebase:client:web</c>. Null when the deployment hasn't
    /// seeded the row.
    /// </summary>
    public FirebaseWebClientConfig? Web { get; set; }
}

/// <summary>
/// Android Firebase client-init payload. Field names mirror the shape the Android
/// <c>FirebaseOptions.Builder</c> consumes so the client can pass them through
/// verbatim to <c>FirebaseApp.initializeApp(context, options, "[DEFAULT]")</c>.
/// </summary>
/// <param name="ApiKey">Public API key (<c>current_key</c> in <c>google-services.json</c>).
///     Wired into <c>FirebaseOptions.Builder.setApiKey</c>.</param>
/// <param name="ApplicationId">Firebase application ID (<c>mobilesdk_app_id</c> in
///     <c>google-services.json</c>), e.g. <c>1:1234567890:android:abcdef</c>. Wired into
///     <c>FirebaseOptions.Builder.setApplicationId</c>.</param>
/// <param name="ProjectId">Firebase project ID (<c>project_id</c>). Wired into
///     <c>FirebaseOptions.Builder.setProjectId</c>.</param>
/// <param name="GcmSenderId">GCM / FCM sender ID (<c>project_number</c>). Wired into
///     <c>FirebaseOptions.Builder.setGcmSenderId</c>.</param>
/// <param name="StorageBucket">Optional Cloud Storage bucket (<c>storage_bucket</c>).
///     Not required for FCM; included so the same payload can be reused if the client
///     ever wants Firebase Storage.</param>
public sealed record FirebaseAndroidClientConfig(
    string ApiKey,
    string ApplicationId,
    string ProjectId,
    string GcmSenderId,
    string? StorageBucket);

/// <summary>
/// iOS Firebase client-init payload. Field names mirror the shape the iOS
/// <c>FirebaseOptions</c> initialiser consumes.
/// </summary>
/// <param name="ApiKey">Public API key (<c>API_KEY</c> in <c>GoogleService-Info.plist</c>).</param>
/// <param name="GoogleAppId">Firebase application ID (<c>GOOGLE_APP_ID</c>). Required
///     positional argument on <c>FirebaseOptions(googleAppID:gcmSenderID:)</c>.</param>
/// <param name="GcmSenderId">GCM / FCM sender ID (<c>GCM_SENDER_ID</c>). Required
///     positional argument.</param>
/// <param name="ProjectId">Firebase project ID (<c>PROJECT_ID</c>). Set on the
///     initialised options struct.</param>
/// <param name="StorageBucket">Optional Cloud Storage bucket (<c>STORAGE_BUCKET</c>).</param>
/// <param name="BundleId">iOS bundle identifier the Firebase project is registered
///     against (<c>BUNDLE_ID</c>). The SDK cross-checks it against the running app's
///     bundle at init time.</param>
/// <param name="ClientId">Optional OAuth 2.0 client ID (<c>CLIENT_ID</c>) — only needed
///     for deployments that also use Firebase Auth's Google Sign-In. Skippable for
///     FCM-only setups.</param>
public sealed record FirebaseIosClientConfig(
    string ApiKey,
    string GoogleAppId,
    string GcmSenderId,
    string ProjectId,
    string? StorageBucket,
    string BundleId,
    string? ClientId);

/// <summary>
/// Web (wasm) Firebase client-init payload. Field names match the flat JS SDK config
/// object passed to <c>firebase.initializeApp(config)</c>, plus the public VAPID key
/// the FCM JS SDK's <c>getToken({ vapidKey })</c> call requires.
/// </summary>
/// <param name="ApiKey">Public API key.</param>
/// <param name="AuthDomain">Firebase Auth domain (e.g. <c>project-id.firebaseapp.com</c>).
///     Required by the JS SDK even when only using FCM.</param>
/// <param name="ProjectId">Firebase project ID.</param>
/// <param name="StorageBucket">Optional Cloud Storage bucket.</param>
/// <param name="MessagingSenderId">GCM / FCM sender ID — same value as the Android /
///     iOS payloads carry under different names.</param>
/// <param name="AppId">Firebase web application ID (e.g. <c>1:1234567890:web:abcdef</c>).</param>
/// <param name="VapidKey">Public VAPID key from the FCM console (Cloud Messaging →
///     Web configuration → Web Push certificates). Passed to
///     <c>messaging.getToken({ vapidKey })</c> so the browser subscribes to Web Push
///     with the right sender. The matching private half stays on the FCM side; no
///     private material lives in this payload.</param>
public sealed record FirebaseWebClientConfig(
    string ApiKey,
    string AuthDomain,
    string ProjectId,
    string? StorageBucket,
    string MessagingSenderId,
    string AppId,
    string VapidKey);
