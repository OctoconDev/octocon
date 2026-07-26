namespace Interfold.Settings.Contracts.Configuration;

/// <summary>FCM v1 service-account credential — PRIVATE, never surfaced through HTTP.
/// Populated by <c>FcmSecretsPostConfigure</c> from
/// <c>internal.secrets:fcm:service_account_json</c>. Absent → NullFCMService fallback.</summary>
public sealed class FcmConfiguration
{
    public const string SectionName = "Octocon:Fcm";

    public string? ServiceAccountJson { get; set; }
}
