using System.Net;
using System.Text.Json;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class PollsControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    public static IEnumerable<PollType> PollTypes()
    {
        yield return PollType.Vote;
        yield return PollType.Choice;
        yield return PollType.Approval;
    }

    [Test]
    public async Task ErrorResponse_ConflictFormats_IncludeEntityRefAndCode()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-error-format";
        var tooLongTitle = new string('a', 101);

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/polls",
            new CreatePollRequest(tooLongTitle),
            principal);
        var body = await res.Content.ReadAsStringAsync();

        // Wire-shape probes: keep the raw-string assertions here — this test exists to
        // pin the exact `{ "code", "entity_ref", ... }` shape of the error envelope, so
        // deserialising through ErrorResponse would hide any drift in the JSON keys.
        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(body).Contains("\"code\"");
            await Assert.That(body).Contains("\"entity_ref\"");
            await Assert.That(body).Contains("poll:title_too_long");
        }
    }

    [Test]
    public async Task PollValidation_DescriptionTooLong_Returns422()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-poll-desc-validation";
        var tooLongDesc = new string('a', 2001); // Max is 2000

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/polls",
            new CreatePollRequest("ValidTitle", Description: tooLongDesc),
            principal);

        var error = await res.ReadErrorAsync(HttpStatusCode.UnprocessableEntity);
        await Assert.That(error.EntityRef).IsEqualTo("poll:description_too_long");
    }

    [Test]
    [CombinedDataSources]
    public async Task PollType_AllSupportedTypes_RoundTripCorrectly(
        [MethodDataSource(typeof(PollsControllerTests), nameof(PollTypes))] PollType type)
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-poll-types";

        using var createRes = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/polls",
            new CreatePollRequest($"Poll_{type}", Type: type),
            principal);
        var createEnv = await createRes.ReadEnvelopeAsync<PollReadModel>(HttpStatusCode.Created);

        using var getRes = await client.SendAuthedGetAsync($"/api/polls/{createEnv.Data.Id}", principal);
        var getBody = await getRes.Content.ReadAsStringAsync();
        var getEnv = await getRes.ReadEnvelopeAsync<PollReadModel>(HttpStatusCode.OK);

        // Typed round-trip: the deserialised enum must match the value we sent.
        await Assert.That(getEnv.Data.Type).IsEqualTo(type);
        // Wire-shape round-trip: the raw JSON must carry the exact JsonStringEnumMemberName
        // spelling (`"vote"` / `"choice"` / `"approval"`), not the CLR enum name. This is the
        // load-bearing bit for cross-language clients that don't have PollType's converter.
        var expectedWireValue = type switch
        {
            PollType.Vote => "vote",
            PollType.Choice => "choice",
            PollType.Approval => "approval",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Add a wire-form entry when introducing a new PollType."),
        };
        await Assert.That(getBody).Contains($"\"type\":\"{expectedWireValue}\"");
    }

    [Test]
    public async Task PollUpdate_TimeEndNullOnly_ClearsExistingTimeEnd()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-poll-time-end";
        var initialTimeEnd = DateTime.UtcNow.AddHours(1);

        using var createRes = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/polls",
            new CreatePollRequest("TimeEndEdgeCase", TimeEnd: initialTimeEnd),
            principal);
        var createEnv = await createRes.ReadEnvelopeAsync<PollReadModel>(HttpStatusCode.Created);

        // PatchValue<DateTime>.OfNull() emits `"time_end": null` which the server maps to
        // the "clear" branch — a plain `TimeEnd = null` on a nullable field would be
        // indistinguishable from "field absent" on the wire and hit the "unchanged" branch.
        using var patchRes = await client.SendAsJsonAsync(
            HttpMethod.Patch, $"/api/polls/{createEnv.Data.Id}",
            new UpdatePollRequest(TimeEnd: PatchValue<DateTime>.OfNull()),
            principal);
        await Assert.That(patchRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        patchRes.Dispose();

        using var getRes = await client.SendAuthedGetAsync($"/api/polls/{createEnv.Data.Id}", principal);
        var getEnv = await getRes.ReadEnvelopeAsync<PollReadModel>(HttpStatusCode.OK);

        await Assert.That(getEnv.Data.TimeEnd).IsNull();
    }

    [Test]
    public async Task PollValidation_TitleTooLong_Returns422()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-poll-validation";
        var tooLongTitle = new string('a', 101);

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/polls",
            new CreatePollRequest(tooLongTitle),
            principal);

        var error = await res.ReadErrorAsync(HttpStatusCode.UnprocessableEntity);
        await Assert.That(error.EntityRef).IsEqualTo("poll:title_too_long");
    }

    [Test]
    public async Task LegacyRoute_SystemsMePolls_Returns404()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-legacy-routes";

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get,  "/api/systems/me/polls"),
                     (HttpMethod.Post, "/api/systems/me/polls"),
                 })
        {
            HttpResponseMessage res;
            if (method == HttpMethod.Post)
            {
                res = await client.SendAsJsonAsync(method, path, new CreatePollRequest("LegacyPoll"), principal);
            }
            else
            {
                using var req = new HttpRequestMessage(method, path);
                AttachPrincipalAuth(req, client, principal);
                res = await client.SendAsync(req);
            }

            try
            {
                await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }
            finally
            {
                res.Dispose();
            }
        }
    }

    [Test]
    public async Task Idempotency_PollCreate_ReplayStable()
    {
        await RunSoakAsync(fixture.Factory, async (client, key) =>
        {
            return await client.SendAsJsonAsync(
                HttpMethod.Post, "/api/polls",
                new CreatePollRequest("SoakPoll"),
                "soak-default-principal",
                idempotencyKey: key);
        });
    }
}

