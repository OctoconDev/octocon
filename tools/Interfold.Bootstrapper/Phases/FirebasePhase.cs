using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Ingests the four operator-supplied Firebase inputs (google-services.json,
/// GoogleService-Info.plist, firebase-web-config.json, FCM v1 service-account JSON),
/// reshapes each into the wire JSON <c>SecretsBootstrapService</c> deserialises at API
/// startup, and returns them for <see cref="DatabaseInitPhase"/> to fold into
/// <see cref="DatabaseBootstrap.PostgresSeedOptions"/>. Runs before <see cref="DatabaseInitPhase"/>
/// so the internal.secrets rows land in the same pass as the OAuth secrets. Every input
/// is optional (blank path → skip; the API falls back to <c>NullFCMService</c> and
/// <c>/api/settings/firebase-config</c> returns 503 for that platform). Non-blank paths
/// that don't resolve OR fail to parse ARE errors — surface at bootstrap, not first
/// request.</summary>
internal static class FirebasePhase
{
    private static readonly string Phase = BootstrapPhase.Firebase.ToWireName();

    // Mirrors SecretsBootstrapService.FirebaseClientJsonOptions so records round-trip 1:1.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Snake_case inputs: google-services.json and the FCM service-account credential.
    // RespectRequiredConstructorParameters + `required` members turn missing fields
    // into a descriptive JsonException.
    private static readonly JsonSerializerOptions SnakeCaseReaderOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        RespectRequiredConstructorParameters = true,
    };

    // firebase-web-config.json is camelCase from the console; nullable positional params
    // still count as required, so a truncated paste fails loudly at bootstrap.
    private static readonly JsonSerializerOptions CamelCaseReaderOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        RespectRequiredConstructorParameters = true,
    };

    public static Task<FirebaseSeedInputs> RunAsync(
        BootstrapOptions options,
        BootstrapConfig config,
        PhaseLogger logger,
        CancellationToken ct)
    {
        logger.PhaseStart(Phase);

        var section = config.Firebase;
        var result = new FirebaseSeedInputs(
            AndroidClientJson: ParseAndroid(section.AndroidConfigPath, options.OutputDir, logger),
            IosClientJson: ParseIos(section.IosConfigPath, options.OutputDir, logger),
            WebClientJson: ReadWebPassthrough(section.WebConfigPath, options.OutputDir, logger),
            ServiceAccountJson: ReadServiceAccount(section.ServiceAccountPath, options.OutputDir, logger));

        var configured = new[]
        {
            (result.AndroidClientJson,   "android client-init"),
            (result.IosClientJson,       "ios client-init"),
            (result.WebClientJson,       "web client-init"),
            (result.ServiceAccountJson,  "fcm service-account"),
        }.Count(x => !string.IsNullOrWhiteSpace(x.Item1));

        if (configured == 0)
            logger.Info("    no firebase inputs configured — API will disable push and return 503 on /api/settings/firebase-config.");
        else
            logger.Info($"    ingested {configured}/4 firebase input(s).");

        logger.PhaseDone(Phase);
        return Task.FromResult(result);
    }

    /// <summary>Reshapes <c>google-services.json</c> into <see cref="FirebaseAndroidClientConfig"/>.
    /// Uses <c>client[0]</c> — multi-app projects should point at the file their Android
    /// build actually consumes. Array-non-empty checks stay explicit (STJ can't express
    /// "at least one element" via <c>required</c>).</summary>
    private static string? ParseAndroid(string path, string outputDir, PhaseLogger logger)
    {
        var resolved = ResolveOptional(path, outputDir, "androidConfigPath", logger);
        if (resolved is null) return null;

        try
        {
            var input = JsonSerializer.Deserialize<GoogleServicesFile>(
                File.ReadAllText(resolved), SnakeCaseReaderOptions)
                ?? throw new InvalidDataException($"{resolved}: file is empty or JSON null");

            if (input.Client.Length == 0)
                throw new InvalidDataException($"{resolved}: no client entries found");
            var client = input.Client[0];
            if (client.ApiKey.Length == 0)
                throw new InvalidDataException($"{resolved}: no api_key entries found");

            var normalised = new FirebaseAndroidClientConfig(
                ApiKey: client.ApiKey[0].CurrentKey,
                ApplicationId: client.ClientInfo.MobilesdkAppId,
                ProjectId: input.ProjectInfo.ProjectId,
                GcmSenderId: input.ProjectInfo.ProjectNumber,
                StorageBucket: input.ProjectInfo.StorageBucket);
            return JsonSerializer.Serialize(normalised, SerializerOptions);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"Failed to parse {resolved} as google-services.json: {ex.Message}", ex);
        }
    }

    /// <summary>Parses <c>GoogleService-Info.plist</c> into <see cref="FirebaseIosClientConfig"/>.
    /// STORAGE_BUCKET / CLIENT_ID are optional; missing entries serialise as JSON null.</summary>
    private static string? ParseIos(string path, string outputDir, PhaseLogger logger)
    {
        var resolved = ResolveOptional(path, outputDir, "iosConfigPath", logger);
        if (resolved is null) return null;

        try
        {
            var doc = XDocument.Load(resolved);
            var dict = doc.Descendants("dict").FirstOrDefault()
                ?? throw new InvalidDataException($"{resolved}: no <dict> element found");
            var map = ReadPlistDictionary(dict);

            string GetRequired(string key) => map.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)
                ? v
                : throw new InvalidDataException($"{resolved}: required key '{key}' is missing");
            string? GetOptional(string key) => map.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

            var normalised = new FirebaseIosClientConfig(
                ApiKey: GetRequired("API_KEY"),
                GoogleAppId: GetRequired("GOOGLE_APP_ID"),
                GcmSenderId: GetRequired("GCM_SENDER_ID"),
                ProjectId: GetRequired("PROJECT_ID"),
                StorageBucket: GetOptional("STORAGE_BUCKET"),
                BundleId: GetRequired("BUNDLE_ID"),
                ClientId: GetOptional("CLIENT_ID"));
            return JsonSerializer.Serialize(normalised, SerializerOptions);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"Failed to parse {resolved} as GoogleService-Info.plist: {ex.Message}", ex);
        }
    }

    /// <summary>Camel→snake re-serialise of <c>firebase-web-config.json</c> into
    /// <see cref="FirebaseWebClientConfig"/>. Every positional param (including nullable
    /// <c>StorageBucket</c>) is required — a truncated console paste fails at bootstrap.</summary>
    private static string? ReadWebPassthrough(string path, string outputDir, PhaseLogger logger)
    {
        var resolved = ResolveOptional(path, outputDir, "webConfigPath", logger);
        if (resolved is null) return null;

        try
        {
            var web = JsonSerializer.Deserialize<FirebaseWebClientConfig>(
                File.ReadAllText(resolved), CamelCaseReaderOptions)
                ?? throw new InvalidDataException($"{resolved}: file is empty or JSON null");
            return JsonSerializer.Serialize(web, SerializerOptions);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"Failed to parse {resolved} as firebase-web-config.json: {ex.Message}", ex);
        }
    }

    /// <summary>FCM v1 service-account JSON passthrough. Validates the two fields
    /// FirebaseAdmin's <c>GoogleCredential.FromJson</c> requires; the raw bytes are
    /// returned verbatim (any reshape breaks the SDK's signature verification).</summary>
    private static string? ReadServiceAccount(string path, string outputDir, PhaseLogger logger)
    {
        var resolved = ResolveOptional(path, outputDir, "serviceAccountPath", logger);
        if (resolved is null) return null;

        var raw = File.ReadAllText(resolved);
        try
        {
            var stub = JsonSerializer.Deserialize<ServiceAccountStub>(raw, SnakeCaseReaderOptions)
                ?? throw new InvalidDataException($"{resolved}: file is empty or JSON null");
            if (stub.Type != "service_account")
                throw new InvalidDataException($"{resolved}: FCM service-account JSON must have \"type\": \"service_account\"");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Failed to parse {resolved} as JSON: {ex.Message}", ex);
        }
        return raw;
    }

    /// <summary>Flattens a &lt;dict&gt;'s alternating key/value children into a string map.
    /// Only strings and booleans are captured; arrays and nested dicts are ignored so an
    /// augmented Firebase download doesn't fail the phase.</summary>
    private static Dictionary<string, string> ReadPlistDictionary(XElement dict)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var elements = dict.Elements().ToList();
        for (var i = 0; i < elements.Count - 1; i++)
        {
            var keyEl = elements[i];
            if (keyEl.Name.LocalName != "key") continue;
            var key = keyEl.Value;
            var valueEl = elements[i + 1];

            if (valueEl.Name.LocalName == "string")
                map[key] = valueEl.Value;
            else if (valueEl.Name.LocalName is "true" or "false")
                map[key] = valueEl.Name.LocalName;
        }
        return map;
    }

    /// <summary>Blank → skip; non-blank must resolve to a file (a typo shouldn't silently
    /// disable push).</summary>
    private static string? ResolveOptional(string path, string outputDir, string fieldName, PhaseLogger logger)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            logger.Info($"    firebase.{fieldName} not set — skipping.");
            return null;
        }

        var resolved = Path.IsPathRooted(path) ? path : Path.Combine(outputDir, path);
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException(
                $"firebase.{fieldName} points at '{resolved}' but no file exists there. " +
                "Either provide the file or clear the path to skip this input.", resolved);
        }
        return resolved;
    }

    // STJ deserialisation targets for Google's file shapes. `required` +
    // RespectRequiredConstructorParameters replaces hand-written null-guards. The Web input
    // deserialises directly into FirebaseWebClientConfig because its camelCase matches 1:1.

    /// <summary>Only the fields the phase forwards into <see cref="FirebaseAndroidClientConfig"/>
    /// are modelled; analytics/oauth/etc. are ignored.</summary>
    private sealed class GoogleServicesFile
    {
        public required GoogleServicesProjectInfo ProjectInfo { get; init; }
        public required GoogleServicesClient[] Client { get; init; }
    }

    private sealed class GoogleServicesProjectInfo
    {
        public required string ProjectNumber { get; init; }
        public required string ProjectId { get; init; }
        public string? StorageBucket { get; init; }
    }

    private sealed class GoogleServicesClient
    {
        public required GoogleServicesClientInfo ClientInfo { get; init; }
        public required GoogleServicesApiKey[] ApiKey { get; init; }
    }

    private sealed class GoogleServicesClientInfo
    {
        public required string MobilesdkAppId { get; init; }
    }

    private sealed class GoogleServicesApiKey
    {
        public required string CurrentKey { get; init; }
    }

    /// <summary>Minimal projection: two required fields whose absence triggers a
    /// <see cref="JsonException"/>. The full credential is trusted to FirebaseAdmin at API startup.</summary>
    private sealed class ServiceAccountStub
    {
        public required string Type { get; init; }
        public required string ProjectId { get; init; }
    }
}

/// <summary>Result bag threaded into <see cref="DatabaseBootstrap.PostgresSeedOptions"/>.
/// Null → operator didn't configure this input; <see cref="DatabaseBootstrap.PostgresSeeder"/>
/// skips the matching row.</summary>
internal sealed record FirebaseSeedInputs(
    string? AndroidClientJson,
    string? IosClientJson,
    string? WebClientJson,
    string? ServiceAccountJson)
{
    public static FirebaseSeedInputs Empty { get; } = new(null, null, null, null);
}
