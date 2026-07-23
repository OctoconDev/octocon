using System.Net;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class FrontingControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task FrontStart_LegacyIdField_Returns201()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-front-legacy-id";
        var alterId = await CreateAlterAsync(client, principal, "LegacyFrontAlter");

        // Regression: front/start body used to be shape `{ id }`. `FrontStartRequest.Id`
        // carries `[JsonPropertyName("id")]` so the typed record serialises to the same wire
        // shape, which is what this test guards against.
        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/systems/me/front/start",
            new FrontStartRequest(alterId),
            principal);

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task Api_FrontHistoryBetween_IncludesEndedFronts()
    {
        using var client = fixture.Factory.CreateClient();

        // The API's fronting handlers stamp time_start/time_end from the injected TimeProvider,
        // which the test fixture replaces with a FakeTimeProvider pinned at its epoch (see
        // InterfoldWebApplicationFactory). Real wall-clock anchors would fall outside that
        // frame and fronts_by_time WHERE time_start >= ? AND time_start <= ? would return
        // zero rows.
        var now = fixture.Factory.TimeProvider.GetUtcNow();
        var startAnchor = now.AddMinutes(-1).ToUnixTimeSeconds();

        var principal = "phase3-fronting-history";
        var alter = await CreateAlterAsync(client, principal, "test alter");

        using var started = await SendFrontStartAsync(client, alterId: alter, comment: "phase3-history", principal);
        var startedEnv = await started.ReadEnvelopeAsync<FrontStartedResponse>(HttpStatusCode.Created);
        var startedFrontId = startedEnv.Data.FrontId;

        using var ended = await SendFrontEndAsync(client, alterId: alter, principal);
        await Assert.That(ended.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var endAnchor = now.AddMinutes(1).ToUnixTimeSeconds();
        using var betweenResponse = await client.SendAuthedGetAsync($"/api/systems/me/front/between?start={startAnchor}&end={endAnchor}", principal);
        var betweenEnv = await betweenResponse.ReadEnvelopeAsync<IReadOnlyList<FrontHistoryReadModel>>(HttpStatusCode.OK);

        await Assert.That(betweenEnv.Data.Count).IsGreaterThan(0);

        var row = betweenEnv.Data[0];
        using (Assert.Multiple())
        {
            await Assert.That(row.Id).IsEqualTo(startedFrontId);
            await Assert.That(row.Comment).IsEqualTo("phase3-history");
            await Assert.That(row.TimeEnd).IsNotNull();
        }
    }

    // ===== Set-fronting semantics =====
    //
    // The set endpoint must leave the target alter as the sole fronter, regardless of the
    // starting state. The previous implementation rejected with `fronting:already_fronting`
    // when the target was already in the active set (broke promote-among-many) and only
    // published a single FrontingSetEvent (clients never saw the per-alter end events for
    // the alters that were silently dropped from the active set).
    //
    // The four cases below cover every starting state. They assert the post-set active
    // list because that is the observable contract the client cares about; per-alter
    // FrontingEndedEvent emission is exercised by the same code path (the handler will not
    // reach the post-end event publish without ending the alter first).

    [Test]
    public async Task FrontSet_FromNoActiveFronters_StartsTargetAsSoleFronter()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = TestIds.NewSystemId("front-set-empty");
        var target = await CreateAlterAsync(client, principal, "TargetAlter");

        using var setResult = await SendFrontSetAsync(client, target, principal, comment: "from-empty");
        await Assert.That(setResult.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var activeAlters = await ListActiveFrontingAlterIdsAsync(client, principal);
        await Assert.That(activeAlters).IsEquivalentTo(new[] { target });
    }

    [Test]
    public async Task FrontSet_WhenTargetIsAlreadySoleFronter_PreservesFrontIdIdempotently()
    {
        // Regression: set(X) when X is already the only fronter must not reject with
        // `fronting:already_fronting` and must not create a new front row (front_id is
        // the stable client-facing handle into the active row; rewriting it on every
        // idempotent set call would break clients that pinned that id).
        using var client = TestClient.NoRedirect(fixture);

        var principal = TestIds.NewSystemId("front-set-target-only");
        var target = await CreateAlterAsync(client, principal, "TargetAlter");

        using var initialStart = await SendFrontStartAsync(client, alterId: target, comment: "initial", principal);
        var initialEnv = await initialStart.ReadEnvelopeAsync<FrontStartedResponse>(HttpStatusCode.Created);
        var originalFrontId = initialEnv.Data.FrontId;

        using var setResult = await SendFrontSetAsync(client, target, principal, comment: "idempotent");
        await Assert.That(setResult.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var activeFrontIdsByAlter = await ListActiveFrontIdsByAlterAsync(client, principal);
        using (Assert.Multiple())
        {
            await Assert.That(activeFrontIdsByAlter.Keys).IsEquivalentTo(new[] { target });
            await Assert.That(activeFrontIdsByAlter[target])
                .IsEqualTo(originalFrontId)
                .Because("set against an already-sole fronter must preserve the existing front_id.");
        }
    }

    [Test]
    public async Task FrontSet_WhenOtherAltersAreFronting_EndsThemAndStartsTarget()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = TestIds.NewSystemId("front-set-others-only");
        var other = await CreateAlterAsync(client, principal, "OtherAlter");
        var target = await CreateAlterAsync(client, principal, "TargetAlter");

        using var otherStart = await SendFrontStartAsync(client, alterId: other, comment: "to-be-ended", principal);
        await Assert.That(otherStart.StatusCode).IsEqualTo(HttpStatusCode.Created);

        using var setResult = await SendFrontSetAsync(client, target, principal);
        await Assert.That(setResult.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var activeAlters = await ListActiveFrontingAlterIdsAsync(client, principal);
        await Assert.That(activeAlters)
            .IsEquivalentTo(new[] { target })
            .Because("the other alter must be ended so the target is the sole fronter.");
    }

    [Test]
    public async Task FrontSet_WhenTargetAndOthersAreFronting_PreservesTargetAndEndsOthers()
    {
        // The crucial promote-among-many case. Pre-fix this rejected with
        // `fronting:already_fronting`; post-fix it must end the others and keep the
        // target's existing front row intact (preserving front_id and start_time).
        using var client = TestClient.NoRedirect(fixture);

        var principal = TestIds.NewSystemId("front-set-mixed");
        var target = await CreateAlterAsync(client, principal, "TargetAlter");
        var other = await CreateAlterAsync(client, principal, "OtherAlter");

        using var targetStart = await SendFrontStartAsync(client, alterId: target, comment: "stays", principal);
        var targetEnv = await targetStart.ReadEnvelopeAsync<FrontStartedResponse>(HttpStatusCode.Created);
        var targetOriginalFrontId = targetEnv.Data.FrontId;

        using var otherStart = await SendFrontStartAsync(client, alterId: other, comment: "ends", principal);
        await Assert.That(otherStart.StatusCode).IsEqualTo(HttpStatusCode.Created);

        using var setResult = await SendFrontSetAsync(client, target, principal);
        await Assert.That(setResult.StatusCode)
            .IsEqualTo(HttpStatusCode.NoContent)
            .Because("set must succeed when the target is already in a multi-fronter active set; the old `already_fronting` rejection broke this case.");

        var activeFrontIdsByAlter = await ListActiveFrontIdsByAlterAsync(client, principal);
        using (Assert.Multiple())
        {
            await Assert.That(activeFrontIdsByAlter.Keys)
                .IsEquivalentTo(new[] { target })
                .Because("only the target should remain fronting after set.");
            await Assert.That(activeFrontIdsByAlter[target])
                .IsEqualTo(targetOriginalFrontId)
                .Because("the target's existing front_id must be preserved; set should not end-and-restart the target.");
        }
    }

    private static async Task<IReadOnlyList<AlterId>> ListActiveFrontingAlterIdsAsync(HttpClient client, string principal)
    {
        var byAlter = await ListActiveFrontIdsByAlterAsync(client, principal);
        return byAlter.Keys.OrderBy(id => id.Value).ToArray();
    }

    private static async Task<IReadOnlyDictionary<AlterId, FrontId>> ListActiveFrontIdsByAlterAsync(HttpClient client, string principal)
    {
        // Self-view: when the viewer is the system owner the response includes every active
        // front regardless of visibility - the only filter ListActiveGuardedAsync applies is
        // for cross-system viewers (friends/trusted/public). We authenticate as `principal`
        // and query `/api/systems/{principal}/fronting`.
        using var res = await client.SendAuthedGetAsync($"/api/systems/{principal}/fronting", principal);

        var envelope = await res.ReadEnvelopeAsync<IReadOnlyList<FrontActiveReadModel>>(HttpStatusCode.OK);

        // Active fronts come back shaped { alter: { id, ... }, front: { id, alter_id, ... }, primary }.
        // We pull alter_id from `front` rather than `alter.Id` because the latter is the
        // hydrated alter read model and this stays deliberately explicit about which id
        // the fronting row records.
        return envelope.Data.ToDictionary(e => e.Front.AlterId, e => e.Front.Id);
    }
}

