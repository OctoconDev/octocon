using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Models.ImportOperations;

/// <summary>
/// Outcome an import-dispatch controller call reports back to the HTTP caller. Two values:
/// <c>Queued</c> (this dispatch minted a fresh operation) and <c>Running</c> (an import was
/// already in flight and this dispatch collapsed onto it). Wire form is the lowercase enum
/// name so the pre-enum client contract stays byte-identical.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ImportOperationDispatchStatus>))]
public enum ImportOperationDispatchStatus
{
    /// <summary>
    /// This dispatch claimed the per-system slot. A background worker will pick up the
    /// job; the HTTP caller can subscribe to the completion WebSocket frame.
    /// </summary>
    [JsonStringEnumMemberName("queued")]
    Queued,

    /// <summary>
    /// An import was already in flight for this system when the dispatch arrived. The
    /// caller gets the in-flight operation id back and MUST NOT enqueue a second worker
    /// run — the existing job's completion frame answers both callers.
    /// </summary>
    [JsonStringEnumMemberName("running")]
    Running,
}
