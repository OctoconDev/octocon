using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Enums;

/// <summary>
/// The role a running node plays in the cluster.
/// Maps directly to the legacy Elixir <c>NODE_GROUP</c> / <c>FLY_PROCESS_GROUP</c> values.
/// </summary>
/// <remarks>
/// JSON wire values (lowercase enum name): <c>"primary"</c>, <c>"auxiliary"</c>, <c>"sidecar"</c>.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<NodeGroup>))]
public enum NodeGroup
{
    /// <summary>
    /// Runs the HTTP API, background jobs, and owns cluster singletons.
    /// Legacy: <c>primary</c>.
    /// </summary>
    [JsonStringEnumMemberName("primary")]
    Primary,

    /// <summary>
    /// Runs the HTTP API only. No background jobs, no singletons.
    /// Legacy: <c>auxiliary</c>.
    /// </summary>
    [JsonStringEnumMemberName("auxiliary")]
    Auxiliary,

    /// <summary>
    /// Handles CPU-intensive tasks (e.g. image processing, encryption offload).
    /// Does not run a public HTTP server in production.
    /// Legacy: <c>sidecar</c>.
    /// </summary>
    [JsonStringEnumMemberName("sidecar")]
    Sidecar
}
