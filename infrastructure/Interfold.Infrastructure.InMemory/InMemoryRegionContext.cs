using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory;

public sealed class InMemoryRegionContext : IRegionContext
{
    private static readonly ScyllaKeyspace[] Regions = Enum.GetValues<ScyllaKeyspace>();

    public ScyllaKeyspace CurrentRegion { get; }

    public InMemoryRegionContext(ScyllaKeyspace currentRegion = ScyllaKeyspace.Nam)
    {
        CurrentRegion = currentRegion;
    }

    public ScyllaKeyspace ResolveUserRegion(SystemId systemId)
    {
        // default(SystemId) has Value == null. Mirror ScyllaKeyspaceResolver's fallback.
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return CurrentRegion;
        }

        // Strip the region prefix so raw and scoped shapes hash to the same region.
        var normalized = ScopedSystemId.StripRegionPrefix(systemId);
        var index = Math.Abs(normalized.GetHashCode(StringComparison.Ordinal)) % Regions.Length;
        return Regions[index];
    }
}
