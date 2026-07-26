using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Journals.Contracts.Models.Read;

namespace Interfold.Journals.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class JournalsControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task Idempotency_GlobalJournalCreate_ReplayStable()
    {
        await RunSoakAsync(fixture.Factory, async (client, key) =>
        {
            return await client.SendAsJsonAsync(
                HttpMethod.Post, "/api/journals",
                new CreateGlobalJournalRequest("SoakJournal"),
                "soak-default-principal",
                idempotencyKey: key);
        });
    }
}
