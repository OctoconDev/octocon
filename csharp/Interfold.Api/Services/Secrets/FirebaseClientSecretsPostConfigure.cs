using System.Text.Json;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Options;

namespace Interfold.Api.Services.Secrets;

/// <summary>
/// Post-configure step that deserialises the three optional Firebase client-init JSON rows
/// from <c>internal.secrets</c> onto <see cref="FirebaseClientConfiguration"/>.
///
/// <para>
/// Each row is optional — a missing row leaves the matching platform property null and the
/// <c>/api/settings/firebase-config</c> endpoint returns 503 for that platform. A row that
/// is present but malformed is a hard fail so the bad seed surfaces at boot instead of at
/// first fetch. The fail-fast is raised as
/// <see cref="InvalidOperationException"/>, which
/// <see cref="Microsoft.Extensions.Options.OptionsFactory{TOptions}"/> propagates through
/// <c>.ValidateOnStart()</c> just like a validation failure.
/// </para>
/// </summary>
internal sealed class FirebaseClientSecretsPostConfigure(ISecretsSnapshot snapshot)
    : IPostConfigureOptions<FirebaseClientConfiguration>
{
    /// <summary>
    /// System.Text.Json options matching the API's global snake_case-lower policy so a
    /// row seeded from the bootstrapper's normalised JSON (which uses snake_case property
    /// names to mirror the wire contract) round-trips cleanly onto the PascalCase record
    /// properties.
    /// </summary>
    private static readonly JsonSerializerOptions FirebaseClientJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly (SecretsStoreKey Key, Action<FirebaseClientConfiguration, string> Patch)[] FirebaseClientMappings =
    [
        (SecretsStoreKeys.FirebaseClientAndroid, (opts, v) =>
            opts.Android = ParseOrThrow<FirebaseAndroidClientConfig>(SecretsStoreKeys.FirebaseClientAndroid, v)),
        (SecretsStoreKeys.FirebaseClientIos,     (opts, v) =>
            opts.Ios = ParseOrThrow<FirebaseIosClientConfig>(SecretsStoreKeys.FirebaseClientIos, v)),
        (SecretsStoreKeys.FirebaseClientWeb,     (opts, v) =>
            opts.Web = ParseOrThrow<FirebaseWebClientConfig>(SecretsStoreKeys.FirebaseClientWeb, v)),
    ];

    public void PostConfigure(string? name, FirebaseClientConfiguration options)
    {
        if (name != Options.DefaultName)
        {
            return;
        }

        foreach (var (key, patch) in FirebaseClientMappings)
        {
            var value = snapshot.Get(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                patch(options, value);
            }
        }
    }

    /// <summary>
    /// Deserialises a Firebase client-init row into the matching typed record. Empty /
    /// whitespace payloads are treated as "row absent" by the caller so this method only
    /// runs on non-blank strings; a null deserialisation result or any parse exception is
    /// escalated to an <see cref="InvalidOperationException"/> so the API refuses to boot
    /// on a bad seed rather than silently returning 503 forever.
    /// </summary>
    private static T ParseOrThrow<T>(SecretsStoreKey key, string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<T>(json, FirebaseClientJsonOptions);
            if (parsed is null)
            {
                throw new InvalidOperationException(
                    $"[secrets-post-configure] internal.secrets:{key.Value} parsed to null. The row exists but does not deserialise into a {typeof(T).Name}.");
            }
            return parsed;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"[secrets-post-configure] internal.secrets:{key.Value} is malformed JSON. Re-seed via the bootstrapper or fix the row manually before restarting the API.",
                ex);
        }
    }
}
