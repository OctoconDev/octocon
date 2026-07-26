using Interfold.Settings.Contracts.Configuration;
using Interfold.Shared.Api.Services.Secrets;
using Interfold.Shared.Contracts.Secrets;
using Microsoft.Extensions.Options;

namespace Interfold.Settings.Api.Services.Secrets;

/// <summary>
/// Post-configure step that copies the optional <c>fcm:service_account_json</c> row from the
/// snapshot onto <see cref="FcmConfiguration"/>.
///
/// <para>
/// Mirrors <see cref="FirebaseClientSecretsPostConfigure"/>'s shape but has no JSON parsing —
/// the FCM Admin SDK's <c>CredentialFactory.FromJson</c> is the sole consumer of the raw
/// string, so this patcher just forwards the value verbatim. A missing row leaves
/// <see cref="FcmConfiguration.ServiceAccountJson"/> null; the <c>IFCMService</c> DI factory
/// treats that as "Firebase not wired" and falls back to <c>NullFCMService</c> — there is no
/// fail-fast here (unlike the mandatory auth secrets).
/// </para>
/// </summary>
internal sealed class FcmSecretsPostConfigure(ISecretsSnapshot snapshot)
    : IPostConfigureOptions<FcmConfiguration>
{
    public void PostConfigure(string? name, FcmConfiguration options)
    {
        if (name != Options.DefaultName)
        {
            return;
        }

        var value = snapshot.Get(SecretsStoreKeys.FcmServiceAccountJson);
        if (!string.IsNullOrWhiteSpace(value))
        {
            options.ServiceAccountJson = value;
        }
    }
}
