using System.Net;
using Interfold.Alters.Contracts.Models;
using Interfold.Alters.Contracts.Models.Read;
using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Journals.Contracts.Models.Read;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Alters.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class AltersControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task FieldSecurityLevelByRelationship_AppliesCorrectly()
    {
        using var client = TestClient.NoRedirect(fixture);
        await RunFieldVisibilityScenarioAsync(client, "parity-guarded-fields");
    }

    [Test]
    public async Task CustomFields_FieldSecurityLevelByRelationship_AppliesCorrectly()
    {
        // Uses the plain (redirect-following) client rather than TestClient.NoRedirect(fixture);
        // the mirror test above pins the no-redirect codepath, this one pins the default
        // HttpClient shape. The shared body doesn't hit any redirect endpoints, so both clients
        // should surface identical results — a divergence here would surface a redirect-behaviour
        // regression in the guarded read path.
        using var client = fixture.Factory.CreateClient();
        await RunFieldVisibilityScenarioAsync(client, "settings-guarded-fields");
    }

    /// <summary>
    /// Shared 60-line body for the two field-visibility tests. Seeds the quartet, mints four
    /// per-visibility settings fields on the owner, creates a public alter, populates the four
    /// custom-field values, then asserts each viewer level (non-friend / friend / trusted) sees
    /// the exact expected slice of values in the guarded alter read.
    /// </summary>
    /// <remarks>
    /// Both callers pin the *same* server behaviour with a different client (no-redirect vs
    /// default). Extracted so a future assertion tweak (adding a fifth visibility level,
    /// renaming the field marker strings) lands in one place instead of drifting between the
    /// two entry points — which is exactly how the line-45 <c>friend → nonFriend</c> bug slipped
    /// into the tree before this refactor.
    /// </remarks>
    private static async Task RunFieldVisibilityScenarioAsync(HttpClient client, string prefix)
    {
        var (owner, nonFriend, friend, trusted) = await SeedVisibilityQuartetAsync(client, prefix);

        var fieldPublic = await CreateSettingsFieldAsync(client, owner, "FieldPublic", FieldType.Text, VisibilityLevel.Public);
        var fieldFriends = await CreateSettingsFieldAsync(client, owner, "FieldFriends", FieldType.Text, VisibilityLevel.FriendsOnly);
        var fieldTrusted = await CreateSettingsFieldAsync(client, owner, "FieldTrusted", FieldType.Text, VisibilityLevel.TrustedOnly);
        var fieldPrivate = await CreateSettingsFieldAsync(client, owner, "FieldPrivate", FieldType.Text, VisibilityLevel.Private);

        var alterId = await CreateAlterAsync(client, owner, "GuardedFieldsAlter");
        await SetAlterSecurityLevelAsync(client, owner, alterId, VisibilityLevel.Public);
        await UpdateAlterFieldsAsync(client, owner, alterId, new UpdateAlterFieldRequest[]
        {
            new(fieldPublic, "PublicValue"),
            new(fieldFriends, "FriendsValue"),
            new(fieldTrusted, "TrustedValue"),
            new(fieldPrivate, "PrivateValue"),
        });

        using var nonFriendRes = await client.SendAuthedGetAsync($"/api/systems/{owner}/alters/{alterId}", nonFriend);
        var nonFriendBody = await nonFriendRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(nonFriendRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(nonFriendBody).Contains("PublicValue");
            await Assert.That(nonFriendBody).DoesNotContain("FriendsValue");
            await Assert.That(nonFriendBody).DoesNotContain("TrustedValue");
            await Assert.That(nonFriendBody).DoesNotContain("PrivateValue");
        }

        await SendFriendRequestAndAcceptAsync(client, friend, owner);

        using var friendRes = await client.SendAuthedGetAsync($"/api/systems/{owner}/alters/{alterId}", friend);
        var friendBody = await friendRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(friendRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(friendBody).Contains("PublicValue");
            await Assert.That(friendBody).Contains("FriendsValue");
            await Assert.That(friendBody).DoesNotContain("TrustedValue");
            await Assert.That(friendBody).DoesNotContain("PrivateValue");
        }

        await SendFriendRequestAndAcceptAsync(client, trusted, owner);
        await SetFriendTrustAsync(client, owner, trusted);

        using var trustedRes = await client.SendAuthedGetAsync($"/api/systems/{owner}/alters/{alterId}", trusted);
        var trustedBody = await trustedRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(trustedRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(trustedBody).Contains("PublicValue");
            await Assert.That(trustedBody).Contains("FriendsValue");
            await Assert.That(trustedBody).Contains("TrustedValue");
            await Assert.That(trustedBody).DoesNotContain("PrivateValue");
        }
    }



    [Test]
    public async Task OwnerView_ListAndGet_ReturnsAllFieldValuesIncludingPrivate()
    {
        // Regression: the owner-view read paths (/api/systems/me/alters and .../{id}) must return
        // populated field values at every VisibilityLevel — Public through Private. Before the fix,
        // InMemoryAlterRepository fed FriendshipLevel.TrustedFriend through the guarded projection,
        // which stripped Private-tier field definitions and dropped unpopulated ones, so the client
        // never saw updates to Private fields. Scylla + Cassandra already did the right thing.
        using var client = TestClient.NoRedirect(fixture);

        var owner = TestIds.NewSystemId("owner-view-fields");

        var fieldPublic = await CreateSettingsFieldAsync(client, owner, "OwnerFieldPublic", FieldType.Text, VisibilityLevel.Public);
        var fieldFriends = await CreateSettingsFieldAsync(client, owner, "OwnerFieldFriends", FieldType.Text, VisibilityLevel.FriendsOnly);
        var fieldTrusted = await CreateSettingsFieldAsync(client, owner, "OwnerFieldTrusted", FieldType.Text, VisibilityLevel.TrustedOnly);
        var fieldPrivate = await CreateSettingsFieldAsync(client, owner, "OwnerFieldPrivate", FieldType.Text, VisibilityLevel.Private);

        var alterId = await CreateAlterAsync(client, owner, "OwnerViewFieldsAlter");
        await UpdateAlterFieldsAsync(client, owner, alterId, new UpdateAlterFieldRequest[]
        {
            new(fieldPublic, "OwnerPublicValue"),
            new(fieldFriends, "OwnerFriendsValue"),
            new(fieldTrusted, "OwnerTrustedValue"),
            new(fieldPrivate, "OwnerPrivateValue"),
        });

        using var listRes = await client.SendAuthedGetAsync("/api/systems/me/alters", owner);
        var listBody = await listRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(listRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(listBody).Contains("OwnerPublicValue");
            await Assert.That(listBody).Contains("OwnerFriendsValue");
            await Assert.That(listBody).Contains("OwnerTrustedValue");
            await Assert.That(listBody).Contains("OwnerPrivateValue");
        }

        using var getRes = await client.SendAuthedGetAsync($"/api/systems/me/alters/{alterId}", owner);
        var getBody = await getRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(getRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(getBody).Contains("OwnerPublicValue");
            await Assert.That(getBody).Contains("OwnerFriendsValue");
            await Assert.That(getBody).Contains("OwnerTrustedValue");
            await Assert.That(getBody).Contains("OwnerPrivateValue");
        }
    }


    [Test]
    public async Task AlterDelete_CascadesAlterJournalsAndDetachesFromGlobalJournals()
    {
        // Regression: deleting an alter must wipe its alter_journals entries (both view tables
        // in the Scylla repo) and detach it from any global_journal_alters rows, while leaving
        // the global journal itself intact (multiple alters can share a group journal).
        // Before the fix, DeleteAlterCommandHandler called only IAlterRepository.DeleteAsync and
        // left every alter-journal entry orphaned in the database.
        using var client = TestClient.NoRedirect(fixture);

        var principal = TestIds.NewSystemId("parity-alter-delete-cascade");
        var deletedAlterId = await CreateAlterAsync(client, principal, "DeleteMe");
        var keeperAlterId = await CreateAlterAsync(client, principal, "KeepMe");

        // Create an alter-journal entry owned by the alter we're about to delete. After the
        // delete, the per-entry GET must 404 (the cascade wiped it).
        using var alterJournalRes = await client.SendAsJsonAsync(
            HttpMethod.Post, $"/api/systems/me/alters/{deletedAlterId}/journals",
            new CreateAlterJournalRequest("AlterJournalToCascade"),
            principal);
        var alterJournalEnv = await alterJournalRes.ReadEnvelopeAsync<AlterJournalReadModel>(HttpStatusCode.Created);
        var alterJournalEntryId = alterJournalEnv.Data.Id;

        // Create a global journal and attach BOTH alters. After the cascade, the global
        // journal must still exist with the keeper alter still attached.
        using var globalRes = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/journals",
            new CreateGlobalJournalRequest("GroupJournalShared"),
            principal);
        var globalEnv = await globalRes.ReadEnvelopeAsync<JournalReadModel>(HttpStatusCode.Created);
        var globalJournalId = globalEnv.Data.Id;

        foreach (var alterIdToAttach in new[] { deletedAlterId, keeperAlterId })
        {
            using var attachRes = await client.SendAsJsonAsync(
                HttpMethod.Post, $"/api/journals/{globalJournalId}/alter",
                new JournalAlterRequest(alterIdToAttach),
                principal);
            await Assert.That(attachRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        // Confirm setup: both alters attached before the delete.
        using var preDeleteRes = await client.SendAuthedGetAsync($"/api/journals/{globalJournalId}", principal);
        var preDeleteEnv = await preDeleteRes.ReadEnvelopeAsync<JournalReadModel>(HttpStatusCode.OK);
        using (Assert.Multiple())
        {
            await Assert.That(preDeleteEnv.Data.Alters).Contains(deletedAlterId);
            await Assert.That(preDeleteEnv.Data.Alters).Contains(keeperAlterId);
        }

        using var deleteAlterRes = await client.SendAuthedDeleteAsync($"/api/systems/me/alters/{deletedAlterId}", principal);
        await Assert.That(deleteAlterRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // Cascade 1: the per-alter journal entry is gone.
        using var showAlterJournalRes = await client.SendAuthedGetAsync($"/api/systems/me/alters/journals/{alterJournalEntryId}", principal);
        await Assert.That(showAlterJournalRes.StatusCode)
            .IsEqualTo(HttpStatusCode.NotFound)
            .Because("the alter's journal entry should be cascade-deleted along with the alter.");

        // Cascade 2: the global journal still exists, but the deleted alter is no longer in
        // its alter_ids list. The keeper alter must still be attached.
        using var postDeleteRes = await client.SendAuthedGetAsync($"/api/journals/{globalJournalId}", principal);
        var postDeleteEnv = await postDeleteRes.ReadEnvelopeAsync<JournalReadModel>(HttpStatusCode.OK);
        using (Assert.Multiple())
        {
            await Assert.That(postDeleteEnv.Data.Alters).DoesNotContain(deletedAlterId);
            await Assert.That(postDeleteEnv.Data.Alters).Contains(keeperAlterId);
        }
    }


    [Test]
    public async Task AlterCreate_IdempotentReplay_WorksAgainstLiveAdapters()
    {
        using var client = fixture.Factory.CreateClient();

        var systemId = TestIds.NewSystemId("itest", maxLen: 14);
        var idempotencyKey = Guid.NewGuid().ToString("N");

        using var firstRes = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/systems/me/alters",
            new CreateAlterRequest("IntegrationSmoke"),
            systemId,
            idempotencyKey: idempotencyKey);
        var firstEnv = await firstRes.ReadEnvelopeAsync<AlterReadModel>(HttpStatusCode.Created);
        await Assert.That(firstEnv.Replay.GetValueOrDefault()).IsFalse();

        using var secondRes = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/systems/me/alters",
            new CreateAlterRequest("IntegrationSmoke"),
            systemId,
            idempotencyKey: idempotencyKey);
        var secondEnv = await secondRes.ReadEnvelopeAsync<AlterReadModel>(HttpStatusCode.Created);
        await Assert.That(secondEnv.Replay).IsTrue();
    }

    [Test]
    public async Task OperationalHealth_GuardedPaths_GetGuardedAsync_Succeeds()
    {
        using var client = TestClient.NoRedirect(fixture);

        // Query a non-existent alter should return 404, not 500
        var principal = "operational-health-test";
        using var res = await client.SendAuthedGetAsync("/api/systems/me/alters/9999", principal);

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Idempotency_AlterCreate_ReplayStable()
    {
        await RunSoakAsync(fixture.Factory, async (client, key) =>
        {
            return await client.SendAsJsonAsync(
                HttpMethod.Post, "/api/systems/me/alters",
                new CreateAlterRequest("SoakAlter"),
                "soak-default-principal",
                idempotencyKey: key);
        });
    }
}

