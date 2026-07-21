using System.Net;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Read;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Controllers;

/// <summary>
/// Wire-boundary verification for the <c>ValidAlterId</c> refactor: every controller path
/// that previously accepted a nullable <c>AlterId</c> and defaulted it to <c>new(0)</c> so
/// invalid ids could leak past model binding must now short-circuit with a
/// <c>400 { code: "invalid_alter_id" }</c> — the same wire shape callers already handle for
/// every other <c>ErrorResponse</c>.
///
/// <para>
/// The negative-path helpers below build their request bodies through
/// <see cref="TestJson.RawJsonContent"/> because the whole point is to send
/// <c>id: 0</c> / <c>alter_id: 0</c> — values that <see cref="AlterId"/>'s constructor
/// would happily accept in C#, but that must be rejected by ASP.NET Core model
/// binding before the controller action runs. Raw JSON keeps the wire bytes explicit and
/// avoids any typed-code path that would round-trip through <c>ValidAlterId</c> client-side.
/// </para>
///
/// <para>
/// Deliberately isolated to <see cref="InMemoryWebFactoryFixture"/> only (unlike the other
/// controller test classes in this folder that fan out across Scylla + Cassandra +
/// InMemory). The behaviour under test is a framework-level model-binding short-circuit —
/// the request never reaches the persistence layer, so parameterising across backends
/// would be pure duplicated wire cost. Restricting to InMemory also lets these tests run
/// on a workstation without Docker: <c>RequiredFixtures.Discover</c> walks the
/// <c>ClassDataSource</c> attributes on the scheduled class set, so with only the InMemory
/// attribute here neither <see cref="ScyllaWebFactoryFixture"/> nor
/// <see cref="CassandraWebFactoryFixture"/> gets discovered — and per
/// <see cref="SharedDbFixture"/>'s docstring, when neither is required that Aspire host is
/// not even instantiated (no Postgres container either). This is the same trick
/// MultiNodeScyllaTests uses to keep its docker footprint minimal.
/// </para>
/// </summary>
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public sealed class AlterIdValidationTests(InMemoryWebFactoryFixture fixture) : BaseEndpointTest
{
    private const string InvalidAlterIdCode = "invalid_alter_id";
    private const string InvalidAlterIdMessage = "Invalid alter ID.";

    // ---------- Fronting ----------

    [Test]
    public async Task FrontStart_ValidId_HappyPathStillWorks()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = TestIds.NewSystemId("alterid-front-ok");
        var alterId = await CreateAlterAsync(client, principal, "HappyPathAlter");

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/systems/me/front/start",
            new FrontStartRequest(alterId),
            principal);

        await Assert.That(res.StatusCode)
            .IsEqualTo(HttpStatusCode.Created)
            .Because("valid alter ids must still succeed; the refactor only tightens the invalid branch.");
    }

    [Test]
    public async Task FrontStart_ZeroId_Returns400InvalidAlterId()
        => await AssertInvalidAlterId(HttpMethod.Post, "/api/systems/me/front/start", "{\"id\":0}");

    [Test]
    public async Task FrontStart_MissingBody_Returns400InvalidAlterId()
        // The record has `Id = default`, so an empty body deserialises to AlterId(0) and
        // ValidAlterId rejects it exactly like an explicit `{ "id": 0 }`.
        => await AssertInvalidAlterId(HttpMethod.Post, "/api/systems/me/front/start", "{}");

    [Test]
    public async Task FrontEnd_ZeroId_Returns400InvalidAlterId()
        => await AssertInvalidAlterId(HttpMethod.Post, "/api/systems/me/front/end", "{\"id\":0}");

    [Test]
    public async Task FrontSet_ZeroId_Returns400InvalidAlterId()
        => await AssertInvalidAlterId(HttpMethod.Post, "/api/systems/me/front/set", "{\"id\":0}");

    [Test]
    public async Task FrontPrimary_ZeroId_Returns400InvalidAlterId()
        // FrontPrimary keeps a nullable AlterId (null clears the primary front) but a non-null
        // out-of-range id must still be rejected: [ValidAlterId(AllowNull = true)] only
        // skips the null check, not the range check.
        => await AssertInvalidAlterId(HttpMethod.Post, "/api/systems/me/front/primary", "{\"id\":0}");

    // ---------- Tags ----------

    [Test]
    public async Task TagAttachAlter_ZeroAlterId_Returns400InvalidAlterId()
    {
        // Tag id is a route token, not a body field — model binding for the body still runs
        // and the ValidAlterId short-circuit fires before the tag lookup, so we can use any
        // syntactically-valid GUID without setting the tag up in the store.
        var tagId = Guid.NewGuid();
        await AssertInvalidAlterId(HttpMethod.Post, $"/api/systems/me/tags/{tagId}/alter", "{\"alter_id\":0}");
    }

    [Test]
    public async Task TagDetachAlter_ZeroAlterId_Returns400InvalidAlterId()
    {
        var tagId = Guid.NewGuid();
        await AssertInvalidAlterId(HttpMethod.Delete, $"/api/systems/me/tags/{tagId}/alter", "{\"alter_id\":0}");
    }

    // ---------- Journals ----------

    [Test]
    public async Task JournalAttachAlter_ZeroAlterId_Returns400InvalidAlterId()
    {
        var journalId = Guid.NewGuid();
        await AssertInvalidAlterId(HttpMethod.Post, $"/api/journals/{journalId}/alter", "{\"alter_id\":0}");
    }

    [Test]
    public async Task JournalDetachAlter_ZeroAlterId_Returns400InvalidAlterId()
    {
        var journalId = Guid.NewGuid();
        await AssertInvalidAlterId(HttpMethod.Delete, $"/api/journals/{journalId}/alter", "{\"alter_id\":0}");
    }

    // ---------- Route alterId ----------

    [Test]
    public async Task AltersShow_ZeroRouteAlterId_Returns400InvalidAlterId()
        => await AssertInvalidAlterIdRoute(HttpMethod.Get, "/api/systems/me/alters/0");

    [Test]
    public async Task AltersUpdate_ZeroRouteAlterId_Returns400InvalidAlterId()
        => await AssertInvalidAlterIdRoute(HttpMethod.Patch, "/api/systems/me/alters/0", "{\"name\":\"ignored\"}");

    [Test]
    public async Task AltersDelete_ZeroRouteAlterId_Returns400InvalidAlterId()
        => await AssertInvalidAlterIdRoute(HttpMethod.Delete, "/api/systems/me/alters/0");

    [Test]
    public async Task AlterJournalsIndex_ZeroRouteAlterId_Returns400InvalidAlterId()
        => await AssertInvalidAlterIdRoute(HttpMethod.Get, "/api/systems/me/alters/0/journals");

    [Test]
    public async Task PublicSystemsShowAlter_ZeroRouteAlterId_Returns400InvalidAlterId()
    {
        var systemId = TestIds.NewSystemId("public-alterid");
        await AssertInvalidAlterIdRoute(HttpMethod.Get, $"/api/systems/{systemId}/alters/0");
    }

    // ---------- helpers ----------

    private async Task AssertInvalidAlterId(HttpMethod method, string path, string rawJsonBody)
    {
        using var client = TestClient.NoRedirect(fixture);

        // A fresh principal per call keeps the negative tests independent of any tag / journal
        // rows created by other tests in the same session — the model-binding 400 fires
        // before the controller reads persistence, but authorisation still runs, so we need
        // a legitimate token bound to a real user row.
        var principal = TestIds.NewSystemId("alterid-neg");
        await EnsureUserExistsAsync(client, principal);

        using var res = await client.SendRawJsonAsync(method, path, rawJsonBody, principal);
        var error = await res.ReadErrorAsync(HttpStatusCode.BadRequest);

        using (Assert.Multiple())
        {
            await Assert.That(error.Code.Value)
                .IsEqualTo(InvalidAlterIdCode)
                .Because("the ErrorResponse code must survive the DataAnnotation → ErrorResponse mapping.");

            await Assert.That(error.Error)
                .IsEqualTo(InvalidAlterIdMessage)
                .Because("the error message must be the sentinel used by ValidationErrorCodeRegistry.");
        }
    }

    private async Task AssertInvalidAlterIdRoute(HttpMethod method, string path, string? rawJsonBody = null)
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = TestIds.NewSystemId("alterid-route-neg");
        await EnsureUserExistsAsync(client, principal);

        HttpResponseMessage res;
        if (rawJsonBody is null)
        {
            using var req = new HttpRequestMessage(method, path);
            AttachPrincipalAuth(req, client, principal);
            res = await client.SendAsync(req);
        }
        else
        {
            res = await client.SendRawJsonAsync(method, path, rawJsonBody, principal);
        }

        try
        {
            var error = await res.ReadErrorAsync(HttpStatusCode.BadRequest);

            using (Assert.Multiple())
            {
                await Assert.That(error.Code.Value)
                    .IsEqualTo(InvalidAlterIdCode)
                    .Because("the ErrorResponse code must survive route-parameter validation mapping.");

                await Assert.That(error.Error)
                    .IsEqualTo(InvalidAlterIdMessage)
                    .Because("the error message must be the sentinel used by ValidationErrorCodeRegistry.");
            }
        }
        finally
        {
            res.Dispose();
        }
    }
}

