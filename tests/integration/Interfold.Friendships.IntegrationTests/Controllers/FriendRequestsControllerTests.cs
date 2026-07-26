using System.Net;
using Interfold.Friendships.Contracts.Models.Read;
using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Shared.Contracts;

namespace Interfold.Friendships.IntegrationTests.Controllers;

[Category("Friendships")]
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class FriendRequestsControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    private static string UniqueId(string prefix) => TestIds.NewSystemId(prefix, maxLen: 24);

    [Test]
    public async Task FriendRequests_FullLifecycle_Succeeds()
    {
        using var client = fixture.Factory.CreateClient();
        var userA = UniqueId("fr-life-a");
        var userB = UniqueId("fr-life-b");

        await EnsureUserExistsAsync(client, userA);
        await EnsureUserExistsAsync(client, userB);

        // 1. Initial State: No requests
        using var initRes = await client.SendAuthedGetAsync("/api/friend-requests", userA);
        var initEnvelope = await initRes.ReadEnvelopeAsync<FriendRequestIndexReadModel>(HttpStatusCode.OK);
        await Assert.That(initEnvelope.Data.Incoming.Count).IsEqualTo(0);
        await Assert.That(initEnvelope.Data.Outgoing.Count).IsEqualTo(0);

        // 2. Send Friend Request: A -> B
        using var sendRes = await client.SendAsJsonAsync(
            HttpMethod.Put, $"/api/friend-requests/{userB}",
            new object(),
            userA);
        await Assert.That(sendRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // 3. Verify outgoing on A
        using var aRes = await client.SendAuthedGetAsync("/api/friend-requests", userA);
        var aEnvelope = await aRes.ReadEnvelopeAsync<FriendRequestIndexReadModel>(HttpStatusCode.OK);
        await Assert.That(aEnvelope.Data.Outgoing.Count).IsEqualTo(1);
        await Assert.That(aEnvelope.Data.Outgoing[0].System.Id.Value).IsEqualTo(userB);

        // 4. Verify incoming on B
        using var bRes = await client.SendAuthedGetAsync("/api/friend-requests", userB);
        var bEnvelope = await bRes.ReadEnvelopeAsync<FriendRequestIndexReadModel>(HttpStatusCode.OK);
        await Assert.That(bEnvelope.Data.Incoming.Count).IsEqualTo(1);
        await Assert.That(bEnvelope.Data.Incoming[0].System.Id.Value).IsEqualTo(userA);

        // 5. Cancel Friend Request: A cancels
        using var cancelRes = await client.SendAuthedDeleteAsync($"/api/friend-requests/{userB}", userA);
        await Assert.That(cancelRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // 6. Verify empty on A
        using var aAfterCancelRes = await client.SendAuthedGetAsync("/api/friend-requests", userA);
        var aAfterCancelEnvelope = await aAfterCancelRes.ReadEnvelopeAsync<FriendRequestIndexReadModel>(HttpStatusCode.OK);
        await Assert.That(aAfterCancelEnvelope.Data.Outgoing.Count).IsEqualTo(0);

        // 7. Send Friend Request again: A -> B
        using var sendRes2 = await client.SendAsJsonAsync(
            HttpMethod.Put, $"/api/friend-requests/{userB}",
            new object(),
            userA);
        await Assert.That(sendRes2.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // 8. Reject Friend Request: B rejects A's request
        using var rejectRes = await client.SendAsJsonAsync(
            HttpMethod.Post, $"/api/friend-requests/{userA}/reject",
            new object(),
            userB);
        await Assert.That(rejectRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // 9. Verify empty on B
        using var bAfterRejectRes = await client.SendAuthedGetAsync("/api/friend-requests", userB);
        var bAfterRejectEnvelope = await bAfterRejectRes.ReadEnvelopeAsync<FriendRequestIndexReadModel>(HttpStatusCode.OK);
        await Assert.That(bAfterRejectEnvelope.Data.Incoming.Count).IsEqualTo(0);

        // 10. Send Friend Request again: A -> B
        using var sendRes3 = await client.SendAsJsonAsync(
            HttpMethod.Put, $"/api/friend-requests/{userB}",
            new object(),
            userA);
        await Assert.That(sendRes3.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // 11. Accept Friend Request: B accepts A's request
        using var acceptRes = await client.SendAsJsonAsync(
            HttpMethod.Post, $"/api/friend-requests/{userA}/accept",
            new object(),
            userB);
        await Assert.That(acceptRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // 12. Verify they are friends
        using var friendshipsRes = await client.SendAuthedGetAsync("/api/friends", userA);
        var friendshipsEnvelope = await friendshipsRes.ReadEnvelopeAsync<IReadOnlyList<FriendshipReadModel>>(HttpStatusCode.OK);
        await Assert.That(friendshipsEnvelope.Data.Count).IsEqualTo(1);
        await Assert.That(friendshipsEnvelope.Data[0].Friend.Id.Value).IsEqualTo(userB);
    }

    [Test]
    public async Task SelfActions_ReturnBadRequest()
    {
        using var client = fixture.Factory.CreateClient();
        var user = UniqueId("fr-self");
        await EnsureUserExistsAsync(client, user);

        // Send to self
        using var sendRes = await client.SendAsJsonAsync(
            HttpMethod.Put, $"/api/friend-requests/{user}",
            new object(),
            user);
        await Assert.That(sendRes.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var sendError = await sendRes.ReadErrorAsync(HttpStatusCode.BadRequest);
        await Assert.That(sendError.Code).IsEqualTo(ErrorCodes.CannotSendSelf);

        // Cancel to self
        using var cancelRes = await client.SendAuthedDeleteAsync($"/api/friend-requests/{user}", user);
        await Assert.That(cancelRes.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var cancelError = await cancelRes.ReadErrorAsync(HttpStatusCode.BadRequest);
        await Assert.That(cancelError.Code).IsEqualTo(ErrorCodes.CannotCancelSelf);

        // Accept from self
        using var acceptRes = await client.SendAsJsonAsync(
            HttpMethod.Post, $"/api/friend-requests/{user}/accept",
            new object(),
            user);
        await Assert.That(acceptRes.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var acceptError = await acceptRes.ReadErrorAsync(HttpStatusCode.BadRequest);
        await Assert.That(acceptError.Code).IsEqualTo(ErrorCodes.CannotAcceptSelf);

        // Reject from self
        using var rejectRes = await client.SendAsJsonAsync(
            HttpMethod.Post, $"/api/friend-requests/{user}/reject",
            new object(),
            user);
        await Assert.That(rejectRes.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var rejectError = await rejectRes.ReadErrorAsync(HttpStatusCode.BadRequest);
        await Assert.That(rejectError.Code).IsEqualTo(ErrorCodes.CannotRejectSelf);
    }
}