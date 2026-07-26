using Cassandra;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.IntegrationTests.TestServices;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.IntegrationTests.Services.Scylla;

/// <summary>
/// Locks the fix for the SP-import "today-date" bug: when
/// <c>current_fronts.time_start</c> is somehow stored as <c>NULL</c> (partial-batch failure,
/// manual fixup, schema drift), the next <see cref="IFrontingRepository.EndAsync"/> must
/// refuse the close rather than silently stamp today's date into
/// <c>fronts_by_time</c> as the front's <c>time_start</c>.
///
/// Scylla-only because the corruption (a stored row with <c>time_start = NULL</c>) can't be
/// produced through the public API; the test inserts the row directly via the cluster session
/// and then drives the production <see cref="IFrontingRepository"/> against it. Every
/// identifier in this file is fresh per-test — no real account data lands in the repo.
/// </summary>
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public sealed class ScyllaFrontingRepositoryNullTimeStartTests(ScyllaWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task EndAsync_NullTimeStartInCurrentFronts_ReturnsFalseAndDoesNotStampToday()
    {
        var factory = fixture.Factory;
        using var client = factory.CreateClient();

        // Mint a unique principal — this becomes the system id throughout the test. The
        // synthetic prefix makes leaked rows easy to spot if a future regression drops
        // the cleanup. CreateAlterAsync also ensures the system + users row exist.
        var rawSystemId = TestIds.NewSystemId("sys-null-ts");
        var systemId = new SystemId(rawSystemId);
        var typedAlterId = await CreateAlterAsync(client, rawSystemId, "synthetic-alter");
        var alterId = typedAlterId.Value;

        var frontingRepo = factory.Services.GetRequiredService<IFrontingRepository>();

        // NormalizeSystemId returns the raw string so it can be bound directly into CQL — the
        // DataStax driver has no serializer for the SystemId wrapper.
        var (session, keyspace, normalizedSystemId) = await ScyllaDirectHarness.ResolveAsync(fixture, systemId);

        var frontGuid = Guid.NewGuid();
        var insertedAt = DateTimeOffset.UtcNow;

        // Synthesise the corrupt row directly. time_start is deliberately omitted from the
        // column list so Scylla stores it as NULL — this is the exact shape ScyllaFrontingRepository
        // can't produce on the happy path, but which would silently break EndAsync without
        // the null-time_start guard.
        await session.ExecuteAsync(new SimpleStatement(
            $"INSERT INTO {keyspace}.current_fronts (user_id, alter_id, id, comment, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?)",
            normalizedSystemId,
            alterId,
            frontGuid,
            "synthetic-corrupt-row",
            insertedAt,
            insertedAt));

        var endedAt = DateTimeOffset.UtcNow;
        var endResult = await frontingRepo.EndAsync(systemId, typedAlterId, endedAt);

        // Pull every fronts_by_time row for this synthetic user. With the fix in place
        // GetCurrentFrontRowAsync returns null on the null time_start and EndAsync bails
        // out before touching fronts_by_time at all, so the result set must be empty.
        var historyRows = (await session.ExecuteAsync(new SimpleStatement(
            $"SELECT time_start FROM {keyspace}.fronts_by_time WHERE user_id = ?",
            normalizedSystemId))).ToList();

        using (Assert.Multiple())
        {
            await Assert.That(endResult).IsFalse()
                .Because("EndAsync must report the close as not-applied when current_fronts.time_start is null, instead of silently stamping today.");
            await Assert.That(historyRows.Count).IsEqualTo(0)
                .Because("fronts_by_time must not gain a synthetic-today row when EndAsync sees a corrupt current_fronts entry.");
        }
    }
}
