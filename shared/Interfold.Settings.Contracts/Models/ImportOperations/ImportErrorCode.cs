using System.Text.Json.Serialization;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Settings.Contracts.Models.ImportOperations;

/// <summary>
/// Stable machine codes for terminal import failures, persisted to the
/// <c>import_operations.error_code</c> column (operators grep/alert on the exact strings —
/// the wire spellings pinned by <see cref="JsonStringEnumMemberNameAttribute"/> are frozen).
/// Both paths are typed: writes emit <see cref="EnumWire{TEnum}.ToWire"/> (via the
/// <c>ToWire()</c> extension), reads rehydrate via the tolerant
/// <see cref="EnumWire{TEnum}.TryParse"/> (unknown legacy spellings resolve to
/// <see langword="false"/> so the caller can substitute <see langword="null"/> rather than throw).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ImportErrorCode>))]
public enum ImportErrorCode
{
    /// <summary>The Simply Plural importer returned a graceful failure.</summary>
    [JsonStringEnumMemberName("sp_import_failed")]
    SpImportFailed,

    /// <summary>Simply Plural rejected the supplied token.</summary>
    [JsonStringEnumMemberName("sp_auth_failed")]
    SpAuthFailed,

    /// <summary>Generic fallback when a runner failed without a specific code.</summary>
    [JsonStringEnumMemberName("import_failed")]
    ImportFailed,

    /// <summary>No <c>IImportJobRunner</c> is registered for the operation kind (DI misregistration).</summary>
    [JsonStringEnumMemberName("no_runner")]
    NoRunner,

    /// <summary>The row was stuck in Running when a fresh host booted; failed by the startup sweep.</summary>
    [JsonStringEnumMemberName("host_restart")]
    HostRestart,

    /// <summary>The worker was cancelled mid-job by host shutdown.</summary>
    [JsonStringEnumMemberName("host_shutdown")]
    HostShutdown,

    /// <summary>The runner threw an unhandled exception.</summary>
    [JsonStringEnumMemberName("exception")]
    Exception,

    /// <summary>The runner is a stub; the importer has not been implemented yet.</summary>
    [JsonStringEnumMemberName("unimplemented")]
    Unimplemented,
}
