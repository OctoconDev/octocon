using System.Net;
using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Ops.Api.Models;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Ops.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class NodeRoleControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task NodeRole_DefaultsToAuxiliary_WhenNoEnvVarSet()
    {
        using var client = fixture.Factory.CreateClient();

        using var response = await client.GetAsync("/health/node-role");
        var body = await response.ReadJsonAsync<NodeRoleResponse>(HttpStatusCode.OK);

        using (Assert.Multiple())
        {
            await Assert.That(body.Role).IsEqualTo(NodeGroup.Auxiliary);
            await Assert.That(body.OwnsSingletons).IsFalse();
        }
    }

    [Test]
    public async Task NodeRole_Endpoint_IsAnonymous()
    {
        using var client = fixture.Factory.CreateClient();

        using var response = await client.GetAsync("/health/node-role");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
