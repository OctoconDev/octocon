namespace Interfold.Contracts.Configuration;

/// <summary>
/// FCM v1 service-account credential used by <c>FirebaseFCMService</c> to authenticate to
/// Google FCM when sending fronting-changed push notifications. This is the PRIVATE half of
/// the Firebase story — never surfaced through any HTTP route (contrast with
/// <see cref="FirebaseClientConfiguration"/>, which holds the public client-init payloads).
/// <para>
/// Populated by <c>FcmSecretsPostConfigure</c> from the optional
/// <c>internal.secrets:fcm:service_account_json</c> row, which <c>SecretsPreBuildLoader</c>
/// loads into <c>ISecretsSnapshot</c> before the host builds. A missing row leaves
/// <see cref="ServiceAccountJson"/> null; the <c>IFCMService</c> DI factory in
/// <c>ClusterServiceCollectionExtensions</c> treats that as "Firebase not wired" and hands
/// out <c>NullFCMService</c> instead of <c>FirebaseFCMService</c>. There is no
/// <c>[Required]</c> on this field — the row is opt-in per deployment.
/// </para>
/// </summary>
public sealed class FcmConfiguration
{
    public const string SectionName = "Octocon:Fcm";

    /// <summary>
    /// FCM v1 service-account credential JSON. Sourced from
    /// <c>internal.secrets:fcm:service_account_json</c>. Null when the deployment hasn't
    /// seeded the row (push disabled, <c>NullFCMService</c> takes over).
    /// </summary>
    public string? ServiceAccountJson { get; set; }
}
