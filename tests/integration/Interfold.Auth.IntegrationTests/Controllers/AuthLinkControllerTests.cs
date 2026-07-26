using System.Net;
using Interfold.Auth.Contracts.Ids;
using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Settings.Contracts.Models.Read;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Auth.IntegrationTests.Controllers;

[Category("Auth")]
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class AuthLinkControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    private static string UniqueId(string prefix) => TestIds.NewSystemId(prefix, maxLen: 24);

    [Test]
    public async Task Begin_WithoutRedirectUri_ReturnsBadRequest()
    {
        using var client = TestClient.NoRedirect(fixture);

        using var res = await client.GetAsync("/auth/link/discord");
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        var error = await res.ReadErrorAsync(HttpStatusCode.BadRequest);
        await Assert.That(error.Code).IsEqualTo(ErrorCodes.MissingRedirectUri);
    }

    [Test]
    public async Task Callback_FullFlow_Succeeds()
    {
        // Callback exercises the end-to-end LinkToken → Callback → LinkIdentityToUserAsync
        // pipeline, which needs BOTH the seed (POST-equivalent to the link_token endpoint's
        // Primary-only write) AND the callback to land on the same host / repository
        // instance. WebApplicationFactory<Program> can hand back different IServiceProvider
        // trees across `Factory.Services` vs an HTTP request scope, so a repo access via
        // `Factory.Services` seeds a DIFFERENT InMemory dict than the one the callback
        // resolves against. Using a private factory pinned to NodeGroup=Primary keeps the
        // whole flow inside a single client / single SP, and the /api/settings/link_token
        // endpoint's write branch (SingletonTaskOwner.OwnsTask(...) == true) actually mints
        // the token. Sibling test below carries the same rationale.
        await using var factory = CreatePrimaryNodeFactory();
        using var client = TestClient.NoRedirect(factory);
        var user = UniqueId("link-flow-u");
        await EnsureUserExistsAsync(client, user);

        // 1. Get Link Token for the user (Primary-node write path — enabled by the
        // factory config above so /api/settings/link_token mints instead of returning 503).
        using var tokenRes = await client.SendAuthedGetAsync("/api/settings/link_token", user);
        var tokenEnv = await tokenRes.ReadEnvelopeAsync<LinkTokenReadModel>(HttpStatusCode.OK);
        var token = tokenEnv.Data.Token;
        await Assert.That(token.Value).IsNotEmpty();

        // 2. Perform Link Callback
        var discordIdStr = $"discord-link-{Guid.NewGuid():N}";
        var clientRedirectUri = "https://test.invalid/link/done";

        var callbackRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/auth/link/discord/callback?uid={Uri.EscapeDataString(discordIdStr)}");
        callbackRequest.Headers.Add("Cookie",
            $"octocon_link_token={Uri.EscapeDataString(token.Value)}; octocon_link_redirect_uri={Uri.EscapeDataString(clientRedirectUri)}");

        using var response = await client.SendAsync(callbackRequest);
        await Assert.That(response.StatusCode)
            .Satisfies(x => x is HttpStatusCode.Redirect or HttpStatusCode.Found
                            or HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect
                            or HttpStatusCode.PermanentRedirect)
            .Because($"Expected redirect to client redirect URI, got {(int)response.StatusCode}");

        var location = response.Headers.Location?.ToString();
        await Assert.That(location).IsEqualTo(clientRedirectUri);

        // 3. Verify in repository that identity is linked. Repositories return the scoped
        // wire form (e.g. "nam:link-flow-u-abc") — the raw region tag varies per adapter
        // (InMemory hashes the id, Scylla resolves by the operator-configured keyspace),
        // so compare on the region-stripped raw id, which is stable across all backends.
        var accountRepo = factory.Services.GetRequiredService<IAccountRepository>();
        var linkedSystemId = await accountRepo.TryFindSystemIdByDiscordIdAsync(new DiscordId(discordIdStr));
        await Assert.That(linkedSystemId).IsNotNull();
        await Assert.That(ScopedSystemId.StripRegionPrefix(linkedSystemId!.Value)).IsEqualTo(user);
    }

    [Test]
    public async Task Callback_WhenAlreadyLinkedToAnotherUser_Returns500InternalServerError()
    {
        // See Callback_FullFlow_Succeeds for the private-factory rationale — same story
        // (Primary-node write path for /api/settings/link_token + single-SP seed→callback
        // chain).
        await using var factory = CreatePrimaryNodeFactory();
        using var client = TestClient.NoRedirect(factory);
        var userA = UniqueId("link-err-a");
        var userB = UniqueId("link-err-b");
        await EnsureUserExistsAsync(client, userA);
        await EnsureUserExistsAsync(client, userB);

        // Link user A to discord ID via the domain repository — the write must land on
        // the same singleton graph the HTTP callback below will read from, so resolve it
        // off `factory.Services` (which for a private factory is the ONLY SP tree since
        // there's no cross-factory SP fragmentation to worry about — the private factory
        // is instantiated seconds ago and only this test client has touched it).
        var accountRepo = factory.Services.GetRequiredService<IAccountRepository>();
        var discordIdStr = $"discord-err-{Guid.NewGuid():N}";
        var linkResult = await accountRepo.LinkIdentityToUserAsync(
            ScopedSystemId.Compose(ScyllaKeyspace.Nam, userA).AsSystemId(),
            ProviderIdentity.FromDiscord(new DiscordId(discordIdStr)));
        await Assert.That(linkResult).IsEqualTo(AccountLinkResult.Success);

        // Get link token for user B via HTTP (Primary-node write path).
        using var tokenRes = await client.SendAuthedGetAsync("/api/settings/link_token", userB);
        var tokenEnv = await tokenRes.ReadEnvelopeAsync<LinkTokenReadModel>(HttpStatusCode.OK);
        var token = tokenEnv.Data.Token;

        // Try linking discord ID to user B
        var clientRedirectUri = "https://test.invalid/link/done";
        var callbackRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/auth/link/discord/callback?uid={Uri.EscapeDataString(discordIdStr)}");
        callbackRequest.Headers.Add("Cookie",
            $"octocon_link_token={Uri.EscapeDataString(token.Value)}; octocon_link_redirect_uri={Uri.EscapeDataString(clientRedirectUri)}");

        using var response = await client.SendAsync(callbackRequest);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
    }

    /// <summary>
    /// Build a private, Primary-node-pinned <see cref="InterfoldWebApplicationFactory"/>
    /// that mirrors the parent fixture's backend wiring (Postgres connection string,
    /// CQL endpoint port). Persistence mode is read off
    /// <see cref="InterfoldWebApplicationFactory.PersistenceMode"/> so no fixture-type
    /// switch is needed here — the enum on the shared factory is the single source of
    /// truth, and <see cref="EnumWireExtensions.ToWire{TEnum}"/> inside the factory ctor
    /// produces the wire spelling.
    /// </summary>
    private InterfoldWebApplicationFactory CreatePrimaryNodeFactory()
    {
        var factory = new InterfoldWebApplicationFactory(fixture.Factory.PersistenceMode)
            .WithConfiguration(OctoconEnvKeys.NodeGroup, NodeGroup.Primary.ToWire());

        // CQL-backed fixtures still need the operator-supplied connection details the shared
        // fixture layered onto its own factory. Only Scylla and Cassandra carry this shape;
        // InMemory has no external backend and needs no extra config beyond the mode above.
        (string PostgresConnection, int CqlPort)? backend = fixture switch
        {
            ScyllaWebFactoryFixture s    => (s.Aspire.PostgresConnectionString, s.Aspire.ScyllaPort!.Value),
            CassandraWebFactoryFixture c => (c.Aspire.PostgresConnectionString, c.Aspire.CassandraPort!.Value),
            _ => null,
        };

        if (backend is { } b)
        {
            factory
                .WithConfiguration("OCTOCON_POSTGRES_CONNECTION", b.PostgresConnection)
                .WithConfiguration("OCTOCON_SCYLLA_PORT", b.CqlPort.ToString())
                .WithConfiguration("OCTOCON_SINGLE_SCYLLA_INSTANCE", "true")
                .WithConfiguration("OCTOCON_DB_RETRY_ATTEMPTS", "10")
                .WithConfiguration("OCTOCON_DB_RETRY_INITIAL_DELAY_MS", "500")
                .WithConfiguration("OCTOCON_DB_RETRY_MAX_DELAY_MS", "3000");
        }

        return factory;
    }
}
