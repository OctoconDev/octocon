using Interfold.Settings.Domain;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Api.UnitTests.Support;
using Interfold.Infrastructure.InMemory.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Interfold.Api.UnitTests;

// Coverage for the unbounded fronts read the export path needs — reference exports
// every front that has a time_start (accounts.ex:1215). The two existing reads on
// IFrontingRepository (ListActiveAsync + ListHistoryBetweenAsync) cannot cover that
// contract: ListActiveAsync drops closed rows, ListHistoryBetweenAsync stitches
// fronts_by_time which is only populated on close (see the CQL migration at
// infrastructure/Interfold.Infrastructure.Scylla/Migrations/002_create_interfold_schema.templated.cql:306-320).
public sealed class InMemoryFrontingRepositoryListAllTests
{
    private static readonly SystemId OwnerId = new("owner01");
    private static readonly AlterId AlterOne = new(1);
    private static readonly AlterId AlterTwo = new(2);

    private static InMemoryFrontingRepository BuildRepository()
    {
        var region = new FixedRegionContext(ScyllaKeyspace.Nam);
        var friendships = new InMemoryFriendshipRepository();
        var fields = new InMemorySettingsFieldRepository(region, new ServiceCollection().BuildServiceProvider());
        var alterFieldDefinitions = new AlterFieldDefinitionsAdapter(fields, NullLogger<AlterFieldDefinitionsAdapter>.Instance);
        var polls = new InMemoryPollRepository(region);
        var alters = new InMemoryAlterRepository(region, friendships, fields, alterFieldDefinitions, polls, NullLogger<InMemoryAlterRepository>.Instance);
        return new InMemoryFrontingRepository(region, friendships, alters, NullLogger<InMemoryFrontingRepository>.Instance);
    }

    [Test]
    public async Task ListAllAsync_ReturnsOpenAndClosedFrontsOrderedByStartDesc()
    {
        var repo = BuildRepository();

        var closed = new DateTimeOffset(2024, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var open = new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero);

        _ = await repo.StartAsync(OwnerId, AlterOne, comment: "closed", startedAt: closed);
        await repo.EndAsync(OwnerId, AlterOne, endedAt: closed.AddDays(1));
        _ = await repo.StartAsync(OwnerId, AlterTwo, comment: "open", startedAt: open);

        var all = await repo.ListAllAsync(OwnerId);

        using (Assert.Multiple())
        {
            await Assert.That(all.Count).IsEqualTo(2)
                .Because("ListAllAsync must surface both open and closed fronts; ListActiveAsync/ListHistoryBetweenAsync individually can't.");
            await Assert.That(all[0].TimeStart).IsEqualTo(open)
                .Because("Ordered by time_start descending so the export writes newest fronts first.");
            await Assert.That(all[0].TimeEnd).IsNull();
            await Assert.That(all[1].TimeStart).IsEqualTo(closed);
            await Assert.That(all[1].TimeEnd).IsEqualTo(closed.AddDays(1));
        }
    }

    [Test]
    public async Task ListAllAsync_EmptyStore_ReturnsEmptyList()
    {
        var repo = BuildRepository();
        var all = await repo.ListAllAsync(OwnerId);
        await Assert.That(all.Count).IsEqualTo(0);
    }
}
