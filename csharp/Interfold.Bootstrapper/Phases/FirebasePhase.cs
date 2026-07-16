using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Contracts.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Ingests the four operator-supplied Firebase inputs
/// (<c>google-services.json</c>, <c>GoogleService-Info.plist</c>,
/// <c>firebase-web-config.json</c>, and the FCM v1 service-account JSON), reshapes each
/// into the wire JSON that <c>SecretsBootstrapService</c> deserialises at API startup,
/// and returns them for <see cref="DatabaseInitPhase"/> to fold into
/// <see cref="DatabaseBootstrap.PostgresSeedOptions"/>.
///
/// <para>
/// Runs before <see cref="DatabaseInitPhase"/> so the seed values are already in
/// <see cref="DatabaseBootstrap.PostgresSeedOptions"/> when
/// <see cref="DatabaseBootstrap.PostgresSeeder.BootstrapAsync"/> runs — the
/// <c>internal.secrets</c> rows land in the same pass as the OAuth secrets, keeping
/// admin state and Firebase state consistent.
/// </para>
///
/// <para>
/// Every input is optional. An empty path in <see cref="FirebaseSection"/> means "skip
/// this platform / feature" — the phase logs the skip and moves on, and the matching
/// seed row stays absent. That in turn means the <c>/api/settings/firebase-config</c>
/// endpoint returns 503 for the affected platform and the <c>IFCMService</c> DI factory
/// falls back to <c>NullFCMService</c>. Missing paths are the supported "self-hosted
/// deployment without Firebase" shape and are not an error.
/// </para>
///
/// <para>
/// A non-empty path that doesn't resolve to a file OR that fails to parse IS an error —
/// the phase throws so the operator sees the mistake at bootstrap time rather than at
/// first API request.
/// </para>
/// </summary>
internal static class FirebasePhase
{
    private static readonly string Phase = BootstrapPhase.Firebase.ToWireName();

    /// <summary>
    /// Wire-write options for the seed JSON emitted into <c>internal.secrets</c>. The
    /// snake_case policy + WhenWritingNull mirror the shape
    /// <c>SecretsBootstrapService.FirebaseClientJsonOptions</c> deserialises with, so
    /// serialising a <see cref="FirebaseAndroidClientConfig"/> / <see cref="FirebaseIosClientConfig"/>
    /// / <see cref="FirebaseWebClientConfig"/> here yields JSON that round-trips into
    /// the same record on the API side without any adapter.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Reader for input files whose keys are snake_case: Google's
    /// <c>google-services.json</c> and the FCM v1 service-account credential.
    /// <c>RespectRequiredConstructorParameters</c> + <c>required</c> members on the
    /// input DTOs let STJ raise a descriptive <see cref="JsonException"/> for any
    /// missing required field, which the catch blocks below wrap into the phase's
    /// <see cref="InvalidDataException"/> shape.
    /// </summary>
    private static readonly JsonSerializerOptions SnakeCaseReaderOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>
    /// Reader for <c>firebase-web-config.json</c> — the Firebase console emits camelCase.
    /// With <c>RespectRequiredConstructorParameters = true</c> the positional
    /// <see cref="FirebaseWebClientConfig"/> constructor's parameters are all treated as
    /// required (nullable-annotated params without a default value still count — see
    /// <see cref="JsonSerializerOptions.RespectRequiredConstructorParameters"/> docs),
    /// so a truncated console paste fails loudly at bootstrap instead of seeding a
    /// half-populated row.
    /// </summary>
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

    /// <summary>
    /// Parses a <c>google-services.json</c> into <see cref="GoogleServicesFile"/> and
    /// reshapes it into the flat <see cref="FirebaseAndroidClientConfig"/> the API
    /// deserialises. Extracts the first <c>client[0]</c> entry — multi-app Firebase
    /// projects that ship several <c>google-services.json</c> variants should point
    /// <see cref="FirebaseSection.AndroidConfigPath"/> at the specific file the Android
    /// build consumes. Field presence is enforced by <c>required</c> on the DTO; only
    /// the two array-non-empty checks stay explicit because STJ can't express "at
    /// least one element".
    /// </summary>
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

    /// <summary>
    /// Parses <c>GoogleService-Info.plist</c> via <see cref="XDocument"/>. Extracts the
    /// standard iOS Firebase keys and emits the snake_case JSON the API deserialises
    /// into <see cref="FirebaseIosClientConfig"/>. STORAGE_BUCKET and CLIENT_ID are optional —
    /// missing entries are emitted as JSON <c>null</c>.
    /// </summary>
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

    /// <summary>
    /// Deserialises <c>firebase-web-config.json</c> (the flat camelCase blob emitted by
    /// the Firebase console) directly into the shared
    /// <see cref="FirebaseWebClientConfig"/> record and re-serialises it under the
    /// snake_case wire policy. Every positional parameter of the record — including the
    /// nullable-annotated <c>StorageBucket</c> — is treated as required by STJ
    /// (see <see cref="CamelCaseReaderOptions"/>), so a truncated console paste fails
    /// at bootstrap instead of seeding a half-populated row.
    /// </summary>
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

    /// <summary>
    /// FCM v1 service-account JSON passthrough. Validates the file parses as JSON and
    /// carries the two fields the FirebaseAdmin SDK's <c>GoogleCredential.FromJson</c>
    /// requires (<c>type</c>, <c>project_id</c>) so an obviously-wrong file surfaces at
    /// bootstrap time; the rest of the credential shape is trusted to the SDK. The raw
    /// file bytes are returned verbatim — the credential must not be reshaped or the
    /// SDK's signature verification fails.
    /// </summary>
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

    /// <summary>
    /// Extracts a &lt;dict&gt; child sequence (alternating &lt;key&gt; and value elements) into a
    /// flat map. Only string / bool values are surfaced — the iOS plist keys we care
    /// about are all strings, and any &lt;array&gt; / nested &lt;dict&gt; entries the operator's
    /// file may carry are ignored rather than raising, so a slightly-augmented Firebase
    /// download doesn't fail the phase.
    /// </summary>
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

    /// <summary>
    /// Resolves an optional path from <see cref="FirebaseSection"/>. Blank / whitespace
    /// values are the supported "skip this input" shape; non-blank values must resolve
    /// to an existing file or the phase throws (a typo in the path shouldn't silently
    /// disable push).
    /// </summary>
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

    // ------------------------------------------------------------------------------------
    // Input DTOs — private because they exist purely as a STJ deserialization target for
    // Google's operator-supplied file shapes. `required` + RespectRequiredConstructorParameters
    // does the presence-checking that used to be hand-written null-guards; the phase
    // then converts each DTO into the shared FirebaseAndroidClientConfig record before
    // emitting the seed row. The Web input has no DTO — it deserialises directly into
    // FirebaseWebClientConfig because Google's flat camelCase shape matches the record
    // 1:1.
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Top-level shape of Google's <c>google-services.json</c>. Only the fields the
    /// phase forwards into <see cref="FirebaseAndroidClientConfig"/> are modelled;
    /// everything else (analytics config, oauth clients, etc.) is ignored.
    /// </summary>
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

    /// <summary>
    /// Minimal projection of the FCM v1 service-account credential — just the two
    /// fields the phase checks before seeding the row. Both are <c>required</c> so a
    /// non-service-account JSON blob (or a truncated one) triggers a
    /// <see cref="JsonException"/> at deserialization time. The full credential shape
    /// is trusted to the FirebaseAdmin SDK, which parses the raw JSON at API startup.
    /// </summary>
    private sealed class ServiceAccountStub
    {
        public required string Type { get; init; }
        public required string ProjectId { get; init; }
    }
}

/// <summary>
/// Immutable bag returned by <see cref="FirebasePhase.RunAsync"/> and threaded into
/// <see cref="DatabaseBootstrap.PostgresSeedOptions"/> by <see cref="DatabaseInitPhase"/>.
/// Every field is nullable — <c>null</c> means "operator didn't configure this input"
/// and the matching seed row will end up with an empty selector, so
/// <see cref="DatabaseBootstrap.PostgresSeeder"/> skips it entirely.
/// </summary>
internal sealed record FirebaseSeedInputs(
    string? AndroidClientJson,
    string? IosClientJson,
    string? WebClientJson,
    string? ServiceAccountJson)
{
    public static FirebaseSeedInputs Empty { get; } = new(null, null, null, null);
}
