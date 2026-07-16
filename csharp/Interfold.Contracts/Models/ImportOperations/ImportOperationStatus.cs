using System.Text.Json.Serialization;
using Interfold.Contracts.Enums;

namespace Interfold.Contracts.Models.ImportOperations;

/// <summary>
/// Lifecycle of an asynchronous third-party import (SP or PK). Persisted in the
/// <c>import_operations.status</c> column via <see cref="EnumWire{TEnum}"/>, with the
/// wire spelling of each member pinned by <see cref="JsonStringEnumMemberNameAttribute"/>
/// so a C# rename can't drift the DB format. Reads are case-insensitive to match the
/// historical tolerant <c>Enum.TryParse</c> behaviour.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ImportOperationStatus>))]
public enum ImportOperationStatus
{
    /// <summary>
    /// The operation row exists and a worker has been signalled to start, but execution
    /// has not yet begun. This is the state the row is created in by
    /// <c>IImportOperationRepository.TryClaimAsync</c>. A row should not stay in this
    /// state for long; if it does, the worker has not picked it up (host bottleneck) or
    /// the worker process crashed before transitioning to <see cref="Running"/>.
    /// </summary>
    [JsonStringEnumMemberName("Queued")]
    Queued,

    /// <summary>
    /// The background worker has begun executing the import. Transitioned via
    /// <c>MarkRunningAsync</c>. Stale rows in this state are recovered by the
    /// background service's startup sweep (any <c>Running</c> row whose
    /// <c>started_at</c> is older than the configured ceiling is rewritten to
    /// <see cref="Failed"/> with a host-restart error code).
    /// </summary>
    [JsonStringEnumMemberName("Running")]
    Running,

    /// <summary>
    /// Terminal: the importer returned <c>Success = true</c>. <c>alter_count</c> is
    /// populated. The completion event has been published on the cluster bus.
    /// </summary>
    [JsonStringEnumMemberName("Succeeded")]
    Succeeded,

    /// <summary>
    /// Terminal: the importer either returned <c>Success = false</c> (graceful failure
    /// like auth or encryption mismatch) or threw. <c>error_code</c> and
    /// <c>error_message</c> are populated. The failure event has been published on the
    /// cluster bus.
    /// </summary>
    [JsonStringEnumMemberName("Failed")]
    Failed,
}
