using System.Net;
using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Ops.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Ops.IntegrationTests.Controllers;

/// <summary>
/// Tests covering configuration values that <see cref="WebApplication.Build"/> snapshots once
/// at startup and never re-reads. Each test instantiates its own
/// <see cref="InterfoldWebApplicationFactory"/> (no <c>[ClassDataSource]</c>, no shared
/// fixture) because the relevant settings - cluster role binding via
/// <see cref="IOptions{ClusterConfiguration}"/> and OAuth scheme registration via
/// <c>AddInterfoldAuthChallengeSchemes</c> - are committed during the host build pipeline,
/// not via <see cref="IOptionsMonitor{T}"/>. Since the rest of the suite consolidates onto
/// <see cref="SharedDbFixture"/>, isolating these four tests behind their own factory keeps
/// the parallel session footprint to a single unavoidable per-test InMemory host.
/// </summary>
public sealed class ClusterRoleAndOAuthRegistrationTests : BaseEndpointTest
{
    [Test]
    public async Task NodeRole_ReturnsPrimary_WhenOctoconNodeGroupIsPrimary()
    {
        using var factory = new InterfoldWebApplicationFactory(PersistenceMode.InMemory)
            .WithConfiguration("OCTOCON_NODE_GROUP", "primary");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/node-role");
        var body = await response.ReadJsonAsync<NodeRoleResponse>(HttpStatusCode.OK);

        using (Assert.Multiple())
        {
            await Assert.That(body.Role).IsEqualTo(NodeGroup.Primary);
            await Assert.That(body.OwnsSingletons).IsTrue();
        }
    }

    [Test]
    public async Task NodeRole_ReturnsPrimary_WhenFlyProcessGroupIsPrimary()
    {
        using var factory = new InterfoldWebApplicationFactory(PersistenceMode.InMemory)
            .WithConfiguration("FLY_PROCESS_GROUP", "primary");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/node-role");
        var body = await response.ReadJsonAsync<NodeRoleResponse>(HttpStatusCode.OK);

        await Assert.That(body.Role).IsEqualTo(NodeGroup.Primary);
    }

    [Test]
    public async Task NodeRole_FlyProcessGroup_TakesPrecedenceOver_OctoconNodeGroup()
    {
        using var factory = new InterfoldWebApplicationFactory(PersistenceMode.InMemory)
            .WithConfiguration("OCTOCON_NODE_GROUP", "primary")
            .WithConfiguration("FLY_PROCESS_GROUP", "sidecar");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/node-role");
        var body = await response.ReadJsonAsync<NodeRoleResponse>(HttpStatusCode.OK);

        using (Assert.Multiple())
        {
            await Assert.That(body.Role).IsEqualTo(NodeGroup.Sidecar);
            await Assert.That(body.OwnsSingletons).IsFalse();
        }
    }

    [Test]
    public async Task Api_AuthRequest_IssuesChallengeRedirect_WhenChallengeEnabledAndSchemeConfigured()
    {
        // The Google challenge scheme + endpoint are baked into the API as constants — see
        // OAuthChallengeServiceCollectionExtensions. The scheme is only registered when
        // OCTOCON_GOOGLE_OAUTH_CLIENT_ID is set (the operator's "I want to use Google"
        // signal); the test pins it here so the scheme registers and the controller issues
        // a real challenge. Because scheme registration is a one-shot startup step rather
        // than IOptionsMonitor-bound, this test owns its own factory.
        using var factory = new InterfoldWebApplicationFactory(PersistenceMode.InMemory)
            .WithConfiguration("OCTOCON_GOOGLE_OAUTH_CLIENT_ID", "test-client-id");

        using var client = TestClient.NoRedirect(factory);

        using var response = await client.GetAsync("/auth/google");

        var locationHeader = response.Headers.Location;
        var location = locationHeader!.ToString();
        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found).IsTrue();
            await Assert.That(locationHeader).IsNotNull();
            await Assert.That(location.StartsWith("https://accounts.google.com/o/oauth2/v2/auth", StringComparison.Ordinal)).IsTrue();
            await Assert.That(location.Contains("%2Fauth%2Fgoogle%2Fcallback", StringComparison.Ordinal)).IsTrue();
        }
    }
}
