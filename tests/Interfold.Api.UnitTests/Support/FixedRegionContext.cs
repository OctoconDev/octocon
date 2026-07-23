using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Api.UnitTests.Support;

internal sealed class FixedRegionContext(ScyllaKeyspace region) : IRegionContext
{
    public ScyllaKeyspace CurrentRegion { get; } = region;
    public ScyllaKeyspace ResolveUserRegion(SystemId systemId) => CurrentRegion;
}
