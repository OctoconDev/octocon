using System.Text.Json;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>
/// Pure static scanner powering the "auto-detect from folder" branch of the interactive
/// Firebase wizard in <see cref="Phases.ConfigPhase"/>. Given a folder full of
/// operator-downloaded Firebase artefacts, resolves each artefact to its matching
/// <see cref="FirebaseSection"/> path so the operator doesn't have to type four paths
/// by hand.
///
/// <para>
/// Recognition rules per platform:
/// </para>
/// <list type="bullet">
///   <item>Android — file literally named <c>google-services.json</c>.</item>
///   <item>iOS — file literally named <c>GoogleService-Info.plist</c>.</item>
///   <item>Web — file literally named <c>firebase-web-config.json</c>.</item>
///   <item>Service account — glob <c>*-firebase-adminsdk-*.json</c> first (matches
///         Google's downloaded default <c>{project-id}-firebase-adminsdk-{hash}.json</c>);
///         if no glob hit, iterate every remaining <c>*.json</c> and pick the first one
///         whose root has <c>"type": "service_account"</c>. Non-parseable / non-matching
///         files are skipped silently.</item>
/// </list>
///
/// <para>
/// Every resolved path is stamped as absolute so the persisted
/// <c>interfold.bootstrap.json</c> is stable regardless of how <c>Deployment.OutputDir</c>
/// later moves. Kept free of any Spectre / <c>IAnsiConsole</c> dependency so it drives
/// straight from unit tests with a temp directory.
/// </para>
/// </summary>
public static class FirebaseFolderScanner
{
    private const string AndroidFileName = "google-services.json";
    private const string IosFileName = "GoogleService-Info.plist";
    private const string WebFileName = "firebase-web-config.json";
    private const string ServiceAccountGlob = "*-firebase-adminsdk-*.json";

    /// <summary>
    /// Platform label used in the <see cref="FirebaseFolderScanResult.Missing"/> list
    /// for the Android input.
    /// </summary>
    public const string AndroidPlatform = "android";

    /// <summary>
    /// Platform label used in the <see cref="FirebaseFolderScanResult.Missing"/> list
    /// for the iOS input.
    /// </summary>
    public const string IosPlatform = "ios";

    /// <summary>
    /// Platform label used in the <see cref="FirebaseFolderScanResult.Missing"/> list
    /// for the web input.
    /// </summary>
    public const string WebPlatform = "web";

    /// <summary>
    /// Platform label used in the <see cref="FirebaseFolderScanResult.Missing"/> list
    /// for the FCM v1 service-account credential.
    /// </summary>
    public const string ServiceAccountPlatform = "service_account";

    /// <summary>
    /// Scans <paramref name="folder"/> for the four Firebase artefacts and returns
    /// resolved absolute paths for every one it finds, plus a list of platform labels
    /// (in the fixed order android, ios, web, service_account) for the ones it didn't.
    /// The returned <see cref="FirebaseSection"/> can be written verbatim onto
    /// <see cref="BootstrapConfig.Firebase"/>.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">Thrown when <paramref name="folder"/>
    /// does not resolve to an existing directory. The interactive wizard validates
    /// existence in its prompt so operators normally never see this — it's a fallback
    /// for programmatic callers that skip the prompt validation.</exception>
    public static FirebaseFolderScanResult Scan(string folder)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(
                $"Firebase folder '{folder}' does not exist. Provide the directory containing " +
                "google-services.json / GoogleService-Info.plist / firebase-web-config.json / " +
                "the FCM service-account JSON.");
        }

        var section = new FirebaseSection();
        var missing = new List<string>(4);

        var androidPath = Path.Combine(folder, AndroidFileName);
        if (File.Exists(androidPath)) section.AndroidConfigPath = Path.GetFullPath(androidPath);
        else missing.Add(AndroidPlatform);

        var iosPath = Path.Combine(folder, IosFileName);
        if (File.Exists(iosPath)) section.IosConfigPath = Path.GetFullPath(iosPath);
        else missing.Add(IosPlatform);

        var webPath = Path.Combine(folder, WebFileName);
        if (File.Exists(webPath)) section.WebConfigPath = Path.GetFullPath(webPath);
        else missing.Add(WebPlatform);

        var serviceAccountPath = FindServiceAccount(folder);
        if (serviceAccountPath is not null) section.ServiceAccountPath = serviceAccountPath;
        else missing.Add(ServiceAccountPlatform);

        return new FirebaseFolderScanResult(section, missing);
    }

    /// <summary>
    /// Resolves the FCM v1 service-account credential. Glob first (cheap; matches
    /// Google's default download name), then content-sniff every remaining <c>*.json</c>
    /// (robust for operators who renamed the file). Returns <c>null</c> when no
    /// candidate parses as a service-account credential.
    /// </summary>
    private static string? FindServiceAccount(string folder)
    {
        var globHits = Directory.EnumerateFiles(folder, ServiceAccountGlob, SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (globHits.Count > 0) return Path.GetFullPath(globHits[0]);

        foreach (var candidate in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            if (LooksLikeServiceAccount(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    /// <summary>
    /// True when the JSON file at <paramref name="path"/> parses cleanly and its root
    /// object carries <c>"type": "service_account"</c>. Any parse failure / missing key
    /// / non-string value is treated as "not a service account" (silent skip) — the
    /// scanner is a discovery helper, not a validator, and a well-formed but unrelated
    /// JSON file (e.g. an <c>.eslintrc.json</c> that happened to land in the folder)
    /// shouldn't abort the whole scan.
    /// </summary>
    private static bool LooksLikeServiceAccount(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "service_account";
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Outcome of a <see cref="FirebaseFolderScanner.Scan"/> call. <see cref="Section"/>
/// carries absolute paths for every recognised artefact (empty string for anything
/// missing); <see cref="Missing"/> names the platform labels for what wasn't found so
/// the wizard's summary table can render a "configure these per-file" hint.
/// </summary>
public sealed record FirebaseFolderScanResult(
    FirebaseSection Section,
    IReadOnlyList<string> Missing);
