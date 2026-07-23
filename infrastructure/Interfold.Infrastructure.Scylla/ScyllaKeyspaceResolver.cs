using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.Scylla;

/// <summary>CQL-boundary resolver for regional/global keyspaces and the region-strip
/// normalisation applied to every system id before bind. The <see cref="string"/> return
/// type on <see cref="NormalizeSystemId"/> is a compile-time firewall — widening it back
/// to <see cref="SystemId"/> would compile but fail at bind time with the DataStax
/// "Unknown Cassandra target type" exception.</summary>
public interface IScyllaKeyspaceResolver
{
    string DefaultKeyspace { get; }

    /// <summary>Regional keyspace that owns <paramref name="systemId"/>. Interpolated
    /// verbatim into CQL text at the call site.</summary>
    string ResolveRegionalKeyspace(SystemId systemId);

    string ResolveGlobalKeyspace();

    /// <summary>Region-stripped raw string for direct CQL bind. Keep the primitive
    /// return type — see <see cref="IScyllaKeyspaceResolver"/> for why.</summary>
    string NormalizeSystemId(SystemId systemId);
}

public sealed class ScyllaKeyspaceResolver : IScyllaKeyspaceResolver
{
    private readonly IRegionContext _regionContext;

    public ScyllaKeyspaceResolver(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    // Unwrap ScyllaKeyspace to the lowercase wire name once, at the CQL boundary.
    public string DefaultKeyspace => _regionContext.CurrentRegion.ToWire();

    public string ResolveRegionalKeyspace(SystemId systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return DefaultKeyspace;

        // Canonicalise scoped and raw ids to the same keyspace — otherwise a JWT-scoped
        // principal and its public-route sibling would land in different keyspaces.
        return _regionContext.ResolveUserRegion(new(NormalizeSystemId(systemId))).ToWire();
    }

    public string ResolveGlobalKeyspace() => ScyllaGlobalKeyspace.Name;

    public string NormalizeSystemId(SystemId systemId)
        => ScopedSystemId.StripRegionPrefix(systemId);
}
