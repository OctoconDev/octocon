namespace Interfold.Shared.Contracts.Configuration;

using Interfold.Shared.Contracts.Configuration.Validation;

/// <summary>
/// OpenTelemetry and observability configuration for traces and metrics export.
/// Binds from environment variables with OCTOCON_ prefix.
/// </summary>
public sealed class ObservabilityConfiguration
{
    public const string SectionName = "Octocon:Observability";

    /// <summary>
    /// OpenTelemetry Protocol (OTLP) gRPC endpoint for trace and metrics export.
    /// When set, enables export; when null/empty, metrics remain in-process only.
    /// Example: 'http://localhost:4317'
    /// Env: OCTOCON_OTLP_ENDPOINT
    /// The <see cref="AbsoluteHttpUriAttribute"/> mirrors the bootstrapper's
    /// <c>config.observability.otlpEndpoint</c> rule: null/empty is fine (disables
    /// export); non-empty must parse as an absolute http(s) URL.
    /// </summary>
    [AbsoluteHttpUri]
    public string? OtlpEndpoint { get; set; }
}
