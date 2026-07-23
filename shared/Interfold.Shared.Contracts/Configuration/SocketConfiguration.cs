namespace Interfold.Shared.Contracts.Configuration;

using System.ComponentModel.DataAnnotations;
using Interfold.Shared.Contracts.Configuration.Validation;

/// <summary>
/// WebSocket message batching and performance tuning.
/// Binds from environment variables with OCTOCON_ prefix.
/// </summary>
public sealed class SocketConfiguration
{
    public const string SectionName = "Octocon:Socket";

    /// <summary>
    /// Threshold in bytes for batching outbound WebSocket messages.
    /// When the batched payload reaches this size, it's flushed immediately.
    /// Env: OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD
    /// Default: null (use built-in default)
    /// Bounds mirrored from <see cref="ConfigurationBounds.SocketBatchBytesThresholdMin"/> /
    /// <see cref="ConfigurationBounds.SocketBatchBytesThresholdMax"/>. Null means "use the
    /// built-in default"; only non-null values are range-checked because <c>[Range]</c>
    /// on a nullable int short-circuits on null.
    /// </summary>
    [Range(ConfigurationBounds.SocketBatchBytesThresholdMin, ConfigurationBounds.SocketBatchBytesThresholdMax)]
    public int? BatchBytesThreshold { get; set; }
}
