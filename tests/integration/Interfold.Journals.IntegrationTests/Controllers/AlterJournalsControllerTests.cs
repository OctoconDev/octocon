using System.Net;
using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Journals.Contracts.Models.Read;

namespace Interfold.Journals.IntegrationTests.Controllers;

[Category("Alter Journals")]
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class AlterJournalsControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test, Category("Index")]
    public async Task AlterJournal_ListWhenEmpty_ReturnsDataAsEmptyArray()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-alter-journal-empty-list";
        var alterId = await CreateAlterAsync(client, principal, "NoJournalAlter");

        using var listRes = await client.SendAuthedGetAsync($"/api/systems/me/alters/{alterId}/journals", principal);

        var envelope = await listRes.ReadEnvelopeAsync<IReadOnlyList<AlterJournalReadModel>>(HttpStatusCode.OK);
        await Assert.That(envelope.Data.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AlterJournal_NestedCreate_Returns201WithDataAndReplay()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-alter-journal";
        var alterId = await CreateAlterAsync(client, principal, "JournalHolder");

        // POST /api/systems/me/alters/:id/journals  →  201 + {data, replay}
        using var createRes = await client.SendAsJsonAsync(
            HttpMethod.Post, $"/api/systems/me/alters/{alterId}/journals",
            new CreateAlterJournalRequest("NestedParityJournal"),
            principal);
        var createEnv = await createRes.ReadEnvelopeAsync<AlterJournalReadModel>(HttpStatusCode.Created);
        var entryId = createEnv.Data.Id;

        using (Assert.Multiple())
        {
            await Assert.That(entryId).IsNotEqualTo(default);
            await Assert.That(createEnv.Replay ?? false).IsFalse();
        }

        // GET /api/systems/me/alters/:id/journals  →  200 + {data:[...]}
        using var listRes = await client.SendAuthedGetAsync($"/api/systems/me/alters/{alterId}/journals", principal);
        var listEnv = await listRes.ReadEnvelopeAsync<IReadOnlyList<AlterJournalReadModel>>(HttpStatusCode.OK);
        await Assert.That(listEnv.Data.Count).IsGreaterThan(0);

        // GET /api/systems/me/alters/journals/:journalId  →  200 + {data:{...}}
        using var showRes = await client.SendAuthedGetAsync($"/api/systems/me/alters/journals/{entryId}", principal);
        var showEnv = await showRes.ReadEnvelopeAsync<AlterJournalReadModel>(HttpStatusCode.OK);
        await Assert.That(showEnv.Data.Id).IsEqualTo(entryId);

        // PATCH /api/systems/me/alters/journals/:journalId  →  204
        using var patchRes = await client.SendAsJsonAsync(
            HttpMethod.Patch, $"/api/systems/me/alters/journals/{entryId}",
            new UpdateAlterJournalRequest(Title: "UpdatedParityJournal"),
            principal);
        await Assert.That(patchRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        patchRes.Dispose();

        // DELETE /api/systems/me/alters/journals/:journalId  →  204
        using var deleteRes = await client.SendAuthedDeleteAsync($"/api/systems/me/alters/journals/{entryId}", principal);

        await Assert.That(deleteRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task AlterJournal_ShowAfterDelete_Returns404()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-alter-journal-delete-404";
        var alterId = await CreateAlterAsync(client, principal, "DeleteHolder");

        using var createRes = await client.SendAsJsonAsync(
            HttpMethod.Post, $"/api/systems/me/alters/{alterId}/journals",
            new CreateAlterJournalRequest("JournalToDelete"),
            principal);
        var createEnv = await createRes.ReadEnvelopeAsync<AlterJournalReadModel>(HttpStatusCode.Created);
        var entryId = createEnv.Data.Id;

        (await client.SendAuthedDeleteAsync($"/api/systems/me/alters/journals/{entryId}", principal)).Dispose();

        using var showRes = await client.SendAuthedGetAsync($"/api/systems/me/alters/journals/{entryId}", principal);
        await Assert.That(showRes.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}

