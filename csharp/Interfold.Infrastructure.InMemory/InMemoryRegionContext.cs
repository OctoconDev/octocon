using Interfold.Contracts.Enums;
using Interfold.Domain.Abstractions;
using Interfold.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory;

public sealed class InMemoryRegionContext : IRegionContext
{
    // Derived from the enum so the in-memory hash routing covers every region.
    private static readonly ScyllaKeyspace[] Regions = Enum.GetValues<ScyllaKeyspace>();

    public ScyllaKeyspace CurrentRegion { get; }

    public InMemoryRegionContext(ScyllaKeyspace currentRegion = ScyllaKeyspace.Nam)
    {
        CurrentRegion = currentRegion;
    }

    public ScyllaKeyspace ResolveUserRegion(SystemId systemId)
    {
        // default(SystemId) surfaces Value == null; the ordinary constructor rejects null,
        // but the struct default is still reachable (uninitialised field, GetValueOrDefault
        // on a nullable, etc.). Treat null / empty / whitespace uniformly and fall back to
        // the constructor-supplied CurrentRegion — the ScyllaKeyspaceResolver default path
        // depends on this and would throw otherwise.
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return CurrentRegion;
        }

        // Callers arrive here with either the raw "sys-abc..." shape (route-bound
        // SystemId) or the scoped "nam:sys-abc..." shape (JWT-derived ScopedSystemId, or
        // a downstream Compose call). Hashing whichever shape happened to reach us would
        // produce two different regions for the same principal — the row written under
        // one shape becomes invisible to the read under the other.
        //
        // Strip the region prefix first so the hash is a function of the principal, not
        // of the caller's chosen wire form. Mirrors ScyllaUserRegistryRegionContext.
        var normalized = ScopedSystemId.StripRegionPrefix(systemId);
        var index = Math.Abs(normalized.GetHashCode(StringComparison.Ordinal)) % Regions.Length;
        return Regions[index];
    }
}
