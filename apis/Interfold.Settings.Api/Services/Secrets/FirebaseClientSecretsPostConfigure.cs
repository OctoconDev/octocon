using System.Text.Json;
using Interfold.Settings.Contracts.Configuration;
using Interfold.Shared.Api.Services.Secrets;
using Interfold.Shared.Contracts.Secrets;
using Microsoft.Extensions.Options;

namespace Interfold.Settings.Api.Services.Secrets;

/// <summary>Deserialises the three optional Firebase client-init JSON rows onto
/// <see cref="FirebaseClientConfiguration"/>. Missing → property stays null (endpoint 503s);
/// malformed → boot-time throw so bad seeds surface immediately.</summary>
internal sealed class FirebaseClientSecretsPostConfigure(ISecretsSnapshot snapshot)
    : IPostConfigureOptions<FirebaseClientConfiguration>
{
    // snake_case_lower matches the bootstrapper's normalised JSON shape.
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

    /// <summary>Throws on null deserialise / JsonException so a bad seed fails boot.</summary>
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
