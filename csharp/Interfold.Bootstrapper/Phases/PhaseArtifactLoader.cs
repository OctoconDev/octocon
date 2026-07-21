using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Contracts.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// The two "look up an artifact on disk, log its resolved path, or fail-fast with the
/// canonical phase-failure reason" helpers every phase used to repeat verbatim. Consolidates:
/// <list type="bullet">
///   <item><see cref="LoadRequiredConfigAsync"/> — resolve the config path, error out with
///     the <c>MissingConfig</c> reason if it's absent, else stream-deserialize it.</item>
///   <item><see cref="RequireComposeFileOrFail"/> — locate the emitted compose file, error
///     out with the <c>NoComposeFile</c> reason if it's absent, else log its resolved path
///     and return it.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Previously every phase that needed either artifact reimplemented the same 8-15 line block,
/// with per-site drift in error wording ("Backup requires..." vs "restore requires..." vs
/// "update-images requires...") that we deliberately preserve here through the
/// <c>commandVerb</c> parameter so the operator-visible messages don't change.
/// </para>
/// <para>
/// Both helpers deliberately emit through <see cref="PhaseLogger.PhaseFail"/> before throwing
/// so failure telemetry and structured logs stay consistent. <see cref="DatabaseInitPhase"/>
/// used to skip the <c>PhaseFail</c> call on missing compose; migrating it here also folds it
/// into the same failure-emission contract as everyone else (benign observability upgrade).
/// </para>
/// </remarks>
internal static class PhaseArtifactLoader
{
    /// <summary>
    /// Resolves the bootstrap config path (via <see cref="BootstrapArtifactPaths.ResolveConfigPath"/>),
    /// requires it to exist, and stream-deserializes it. On the missing-file path emits
    /// <see cref="PhaseFailureReasons.MissingConfig"/> and throws with an operator-friendly
    /// message that includes the resolved path and the "Run `bootstrap` first" recovery hint.
    /// </summary>
    /// <param name="commandVerb">
    /// The verb used at the start of the error message ("Backup", "restore", "update-images",
    /// "install-service"). Preserves the exact per-command wording each phase historically used.
    /// </param>
    public static async Task<BootstrapConfig> LoadRequiredConfigAsync(
        BootstrapOptions options,
        PhaseLogger logger,
        string phase,
        string commandVerb,
        CancellationToken ct)
    {
        var configPath = BootstrapArtifactPaths.ResolveConfigPath(options);
        if (!File.Exists(configPath))
        {
            logger.PhaseFail(phase, PhaseFailureReasons.MissingConfig);
            throw new InvalidOperationException(
                $"{commandVerb} requires a populated bootstrap config at {configPath}. " +
                "Run `bootstrap` first.");
        }

        await using var stream = File.OpenRead(configPath);
        return await JsonSerializer.DeserializeAsync(
            stream, BootstrapJsonContext.Default.BootstrapConfig, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Failed to parse {configPath}.");
    }

    /// <summary>
    /// Best-effort config load: resolves the config path, returns <c>null</c> when the file
    /// is missing or cannot be parsed. Unlike <see cref="LoadRequiredConfigAsync"/> this
    /// method never throws or emits a phase-failure log — callers that treat a missing config
    /// as a soft default (e.g. <see cref="LaunchPhase"/> falling back to port 5000) use this
    /// overload.
    /// </summary>
    public static async Task<BootstrapConfig?> TryLoadConfigAsync(
        BootstrapOptions options, CancellationToken ct)
    {
        var configPath = BootstrapArtifactPaths.ResolveConfigPath(options);
        if (!File.Exists(configPath))
            return null;

        try
        {
            await using var stream = File.OpenRead(configPath);
            return await JsonSerializer.DeserializeAsync(
                stream, BootstrapJsonContext.Default.BootstrapConfig, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Locates the emitted compose file
    /// <see cref="BootstrapArtifactPaths.FindComposeFile"/>). Emits
    /// <see cref="PhaseFailureReasons.NoComposeFile"/> and throws with the canonical
    /// operator message when it's absent, otherwise logs the resolved path and returns it.
    /// </summary>
    public static string RequireComposeFileOrFail(
        BootstrapOptions options,
        PhaseLogger logger,
        string phase)
    {
        var composeFile = BootstrapArtifactPaths.FindComposeFile(options.OutputDir);
        if (composeFile is null)
        {
            logger.PhaseFail(phase, PhaseFailureReasons.NoComposeFile);
            throw new InvalidOperationException(
                $"docker-compose.yaml not found under {options.OutputDir}. Run `bootstrap publish` first.");
        }
        logger.Info($"    using compose file {composeFile}");
        return composeFile;
    }

    /// <summary>
    /// Loads the existing secrets file via <see cref="SecretsPhase.LoadExisting"/>, emitting
    /// <see cref="PhaseFailureReasons.MissingSecrets"/> and rethrowing with an operator-friendly
    /// message when the file is absent. Mirrors the shape of <see cref="LoadRequiredConfigAsync"/>.
    /// </summary>
    /// <param name="commandVerb">
    /// The verb used at the start of the error message ("Backup", "restore", etc.).
    /// Preserves the exact per-command wording each phase historically used.
    /// </param>
    public static GeneratedSecrets LoadRequiredSecretsOrFail(
        BootstrapOptions options,
        PhaseLogger logger,
        string phase,
        string commandVerb)
    {
        try
        {
            return SecretsPhase.LoadExisting(options);
        }
        catch (InvalidOperationException ex)
        {
            logger.PhaseFail(phase, PhaseFailureReasons.MissingSecrets);
            throw new InvalidOperationException(
                $"{commandVerb} requires the admin credentials in secrets/secrets.json under {options.OutputDir}. " +
                "Run `bootstrap` first to generate them.", ex);
        }
    }

    /// <summary>
    /// Validates that <see cref="GeneratedSecrets.PostgresAdminPassword"/> is non-empty and
    /// returns the <c>(adminUser, adminPassword)</c> pair. Emits
    /// <see cref="PhaseFailureReasons.MissingAdminPassword"/> and throws when the password is
    /// missing or empty.
    /// </summary>
    public static (string AdminUser, string AdminPassword) RequireAdminPassword(
        GeneratedSecrets secrets,
        PhaseLogger logger,
        string phase)
    {
        var adminUser = $"{secrets.PostgresUser}_admin";
        var adminPassword = secrets.PostgresAdminPassword;
        if (string.IsNullOrEmpty(adminPassword))
        {
            logger.PhaseFail(phase, PhaseFailureReasons.MissingAdminPassword);
            throw new InvalidOperationException(
                "secrets/secrets.json does not contain a PostgresAdminPassword. " +
                "Either it predates DatabaseInitPhase or it was hand-edited; re-run `bootstrap`.");
        }
        return (adminUser, adminPassword);
    }
}
