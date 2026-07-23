using System.Net;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class TagsControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task Idempotency_TagCreate_ReplayStable()
    {
        await RunSoakAsync(fixture.Factory, async (client, key) =>
        {
            return await client.SendAsJsonAsync(
                HttpMethod.Post, "/api/systems/me/tags",
                new CreateTagRequest("SoakTag", ParentTagId: null),
                "soak-default-principal",
                idempotencyKey: key);
        });
    }

    [Test]
    public async Task TagParent_SetAndRemove_Returns204()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-tag-parent";
        var parentTagId = await CreateTagAsync(client, principal, "ParentTag");
        var childTagId = await CreateTagAsync(client, principal, "ChildTag");

        using var setRes = await client.SendAsJsonAsync(
            HttpMethod.Post, $"/api/systems/me/tags/{childTagId}/parent",
            new SetParentRequest(parentTagId),
            principal);

        using var removeRes = await client.SendAsJsonAsync(
            HttpMethod.Delete, $"/api/systems/me/tags/{childTagId}/parent",
            new SetParentRequest(ParentTagId: null),
            principal);

        using (Assert.Multiple())
        {
            await Assert.That(setRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await Assert.That(removeRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }
    }
}
