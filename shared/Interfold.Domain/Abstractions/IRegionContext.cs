using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Domain.Abstractions;

/// <summary>Region resolution for the multi-region topology. Typed as
/// <see cref="ScyllaKeyspace"/>; callers unwrap to the lowercase wire name only at CQL
/// keyspace interpolation. Region-prefix stripping lives on
/// <see cref="ScopedSystemId.StripRegionPrefix(SystemId)"/>.</summary>
public interface IRegionContext
{
    ScyllaKeyspace CurrentRegion { get; }

    ScyllaKeyspace ResolveUserRegion(SystemId systemId);
}
