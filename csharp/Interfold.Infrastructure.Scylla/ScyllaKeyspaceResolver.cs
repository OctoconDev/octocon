using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.Scylla;

/// <summary>
/// CQL-boundary resolver for regional and global keyspaces plus the region-strip
/// normalisation applied to every system id before it reaches a bind slot. The
/// primitive <see cref="string"/> return on <see cref="NormalizeSystemId"/> is the
/// compile-time firewall: bind sites receive the raw string directly, so the
/// <see cref="SystemId"/> wrapper struct cannot slip into a <c>SimpleStatement</c>
/// arg list and re-introduce the "Unknown Cassandra target type" runtime exception
/// the DataStax driver throws for unknown CLR types.
///
/// <para>
/// Inside this file the widen from <see cref="SystemId"/> to <see cref="string"/> uses
/// the implicit conversion operator on <see cref="SystemId"/> rather than an explicit
/// <c>.Value</c> read; the guarantee is identical because every receiving parameter
/// and this method's return are statically typed <see cref="string"/>, so the CLR
/// still narrows to the raw primitive at the sink. Reintroducing the wrapper struct
/// at a bind arg (e.g. by widening <c>NormalizeSystemId</c>'s return to
/// <see cref="SystemId"/>) would compile at the call site but fail at bind time —
/// keep the string return type to preserve the type-level guarantee.
/// </para>
/// </summary>
public interface IScyllaKeyspaceResolver
{
    string DefaultKeyspace { get; }

    /// <summary>
    /// Resolve the regional keyspace that owns the given <paramref name="systemId"/>.
    /// Keyspace names are interpolated verbatim into CQL text at the call site.
    /// </summary>
    string ResolveRegionalKeyspace(SystemId systemId);

    string ResolveGlobalKeyspace();

    /// <summary>
    /// Region-strip a <see cref="SystemId"/> and return the raw underlying <see cref="string"/>
    /// suitable for direct CQL bind. The return type is deliberately the primitive rather
    /// than another <see cref="SystemId"/> so callers cannot accidentally route the wrapper
    /// struct into a <c>SimpleStatement</c> bind slot.
    /// </summary>
    string NormalizeSystemId(SystemId systemId);
}

public sealed class ScyllaKeyspaceResolver : IScyllaKeyspaceResolver
{
    private readonly IRegionContext _regionContext;

    public ScyllaKeyspaceResolver(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    // IRegionContext's resolution APIs are typed ScyllaKeyspace; this resolver is the CQL
    // boundary, so it unwraps to the lowercase keyspace name exactly once here.
    public string DefaultKeyspace => _regionContext.CurrentRegion.ToWire();

    public string ResolveRegionalKeyspace(SystemId systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return DefaultKeyspace;

        // JWT-derived principals arrive scoped (nam:sys-abc) while public-route bindings
        // stay raw (sys-abc). Canonicalise to the stripped raw id and resolve through
        // IRegionContext so both wire forms share one keyspace — otherwise the same
        // principal could be written to nam.* and read from eur.*.
        return _regionContext.ResolveUserRegion(new(NormalizeSystemId(systemId))).ToWire();
    }

    public string ResolveGlobalKeyspace() => ScyllaGlobalKeyspace.Name;

    public string NormalizeSystemId(SystemId systemId)
        => ScopedSystemId.StripRegionPrefix(systemId);
}
