namespace Interfold.Shared.Contracts.Configuration;

/// <summary>Scylla container layout for the AppHost graph
/// (<c>Parameters:scylla-topology</c>). Distinct from <c>DatabaseMode</c> — that's the
/// operator-facing single/multi/cassandra choice; this is the derived Scylla-only layout.</summary>
public enum ScyllaTopology
{
    Single,
    Multi,
}

public static class ScyllaTopologyExtensions
{
    public const string SingleWireValue = "single";
    public const string MultiWireValue = "multi";

    public static string ToWireValue(this ScyllaTopology topology) => topology switch
    {
        ScyllaTopology.Single => SingleWireValue,
        ScyllaTopology.Multi => MultiWireValue,
        _ => throw new ArgumentOutOfRangeException(nameof(topology), topology, "Unhandled ScyllaTopology."),
    };

    /// <summary>Missing / unrecognized values resolve to <see cref="ScyllaTopology.Single"/>,
    /// matching the AppHost's historical <c>== "multi"</c> comparison.</summary>
    public static ScyllaTopology Parse(string? raw)
        => string.Equals(raw, MultiWireValue, StringComparison.OrdinalIgnoreCase)
            ? ScyllaTopology.Multi
            : ScyllaTopology.Single;
}

/// <summary>Wire spellings for Aspire boolean parameter toggles.</summary>
public static class BoolWire
{
    public const string TrueValue = "true";
    public const string FalseValue = "false";

    public static string ToWireValue(bool value) => value ? TrueValue : FalseValue;

    /// <summary>Historical toggle semantics: null/empty → fallback, else "false" (case-insensitive) → off, everything else → on.</summary>
    public static bool ParseToggle(string? raw, bool fallback)
        => string.IsNullOrEmpty(raw)
            ? fallback
            : !string.Equals(raw, FalseValue, StringComparison.OrdinalIgnoreCase);
}
