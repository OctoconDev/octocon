namespace Interfold.Contracts.Configuration;

/// <summary>
/// The Scylla container layout the AppHost graph builds, carried by the
/// <c>Parameters:scylla-topology</c> parameter. Deliberately distinct from
/// <c>DatabaseMode</c> despite the shared "multi" spelling: <c>DatabaseMode</c> is the
/// operator-facing single/multi/cassandra choice, this is the derived Scylla-only layout.
/// </summary>
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

/// <summary>
/// The <c>"true"</c>/<c>"false"</c> wire spellings used by Aspire parameter toggles,
/// replacing inline comparisons scattered across the AppHost and PublishPhase.
/// </summary>
public static class BoolWire
{
    public const string TrueValue = "true";
    public const string FalseValue = "false";

    public static string ToWireValue(bool value) => value ? TrueValue : FalseValue;

    /// <summary>Null/empty resolves to <paramref name="fallback"/>; anything except a
    /// case-insensitive <c>"false"</c> counts as on (the historical toggle semantics).</summary>
    public static bool ParseToggle(string? raw, bool fallback)
        => string.IsNullOrEmpty(raw)
            ? fallback
            : !string.Equals(raw, FalseValue, StringComparison.OrdinalIgnoreCase);
}
