using Cassandra;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Topology;

/// <summary>
/// Tests that a multi-node ScyllaDB cluster (7 regional DCs) correctly spins up
/// and can serve cross-DC queries. Uses a dedicated <see cref="MultiNodeScyllaFixture"/>
/// managed by TUnit.Aspire that shares Postgres with <see cref="SharedDbFixture"/>.
/// </summary>
[ClassDataSource<MultiNodeScyllaFixture>(Shared = SharedType.PerTestSession)]
public sealed class MultiNodeScyllaTests(MultiNodeScyllaFixture fixture)
{
    private static readonly string[] ExpectedRegions = ["nam", "eur", "sam", "sas", "eas", "ocn", "gdpr"];

    [Test]
    public async Task MultiNodeScylla_AllNodesReachUpNormalState()
    {
        await using var sts = await ScyllaTestSession.OpenAsAppAsync("127.0.0.1", fixture.ScyllaPort);
        var session = sts.Session;

        var visibleDcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var localRows = await session.ExecuteAsync(new SimpleStatement("SELECT data_center FROM system.local"));
        foreach (var row in localRows)
        {
            var dc = row.GetValue<string>("data_center");
            if (!string.IsNullOrWhiteSpace(dc))
                visibleDcs.Add(dc.ToLowerInvariant());
        }

        var peerRows = await session.ExecuteAsync(new SimpleStatement("SELECT data_center FROM system.peers"));
        foreach (var row in peerRows)
        {
            var dc = row.GetValue<string>("data_center");
            if (!string.IsNullOrWhiteSpace(dc))
                visibleDcs.Add(dc.ToLowerInvariant());
        }

        foreach (var region in ExpectedRegions)
        {
            await Assert.That(visibleDcs.Contains(region)).IsTrue()
                .Because($"Expected DC '{region}' to be visible in cluster");
        }
    }

    [Test]
    [DependsOn(nameof(MultiNodeScylla_AllNodesReachUpNormalState))]
    public async Task MultiNodeScylla_CrossDcCqlQuerySucceeds()
    {
        await using var sts = await ScyllaTestSession.OpenAsAppAsync("127.0.0.1", fixture.ScyllaPort);
        var namSession = sts.Session;

        // Column names match the live schema in
        // csharp/Interfold.Infrastructure.Scylla/Migrations/002_create_interfold_schema.templated.cql
        // (PRIMARY KEY (user_id) on the `global.user_registry` table); the previous
        // `id` literal would now fail with "Unknown identifier id".
        var testUserId = TestIds.NewSystemId("test", maxLen: 20);

        await namSession.ExecuteAsync(new SimpleStatement(
            "INSERT INTO global.user_registry (user_id, region) VALUES (?, ?)", testUserId, "nam"));

        var result = await namSession.ExecuteAsync(new SimpleStatement(
            "SELECT region FROM global.user_registry WHERE user_id = ?", testUserId));

        var row = result.FirstOrDefault();
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.GetValue<string>("region")).IsEqualTo("nam");

        await namSession.ExecuteAsync(new SimpleStatement(
            "DELETE FROM global.user_registry WHERE user_id = ?", testUserId));
    }
}
