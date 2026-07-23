using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;

namespace Interfold.Api.UnitTests.Support;

internal sealed class FixedRegionContext(ScyllaKeyspace region) : IRegionContext
{
    public ScyllaKeyspace CurrentRegion { get; } = region;
    public ScyllaKeyspace ResolveUserRegion(SystemId systemId) => CurrentRegion;
}
