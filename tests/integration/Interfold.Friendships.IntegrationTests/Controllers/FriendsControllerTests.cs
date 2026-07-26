using System.Net;
using Interfold.Friendships.Contracts.Models.Read;
using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Friendships.IntegrationTests.Controllers;

[Category("Friendships")]
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class FriendsControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    private static string UniqueId(string prefix) => TestIds.NewSystemId(prefix, maxLen: 24);

    [Test]
    public async Task Friendship_FullLifecycle_Succeeds()
    {
        using var client = fixture.Factory.CreateClient();
        var userA = UniqueId("fr-life-a");
        var userB = UniqueId("fr-life-b");

        await EnsureUserExistsAsync(client, userA);
        await EnsureUserExistsAsync(client, userB);

        // 1. Initially they are not friends
        using var showInitRes = await client.SendAuthedGetAsync($"/api/friends/{userB}", userA);
        await Assert.That(showInitRes.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // 2. Establish friendship
        await SendFriendRequestAndAcceptAsync(client, userA, userB);

        // 3. List friends on A
        using var listRes = await client.SendAuthedGetAsync("/api/friends", userA);
        var listEnvelope = await listRes.ReadEnvelopeAsync<IReadOnlyList<FriendshipReadModel>>(HttpStatusCode.OK);
        await Assert.That(listEnvelope.Data.Count).IsEqualTo(1);
        await Assert.That(listEnvelope.Data[0].Friend.Id.Value).IsEqualTo(userB);
        await Assert.That(listEnvelope.Data[0].Friendship.Level).IsEqualTo(FriendshipLevel.Friend);

        // 4. Show friendship details
        using var showRes = await client.SendAuthedGetAsync($"/api/friends/{userB}", userA);
        var showEnvelope = await showRes.ReadEnvelopeAsync<FriendshipReadModel>(HttpStatusCode.OK);
        await Assert.That(showEnvelope.Data.Friend.Id.Value).IsEqualTo(userB);
        await Assert.That(showEnvelope.Data.Friendship.Level).IsEqualTo(FriendshipLevel.Friend);

        // 5. Upgrade to Trust
        using var trustRes = await client.SendAsJsonAsync(
            HttpMethod.Post, $"/api/friends/{userB}/trust",
            new object(),
            userA);
        await Assert.That(trustRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // 6. Verify upgraded status
        using var showTrustedRes = await client.SendAuthedGetAsync($"/api/friends/{userB}", userA);
        var showTrustedEnvelope = await showTrustedRes.ReadEnvelopeAsync<FriendshipReadModel>(HttpStatusCode.OK);
        await Assert.That(showTrustedEnvelope.Data.Friendship.Level).IsEqualTo(FriendshipLevel.TrustedFriend);

        // 7. Downgrade from Trust (Untrust)
        using var untrustRes = await client.SendAsJsonAsync(
            HttpMethod.Post, $"/api/friends/{userB}/untrust",
            new object(),
            userA);
        await Assert.That(untrustRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // 8. Verify downgraded status
        using var showUntrustedRes = await client.SendAuthedGetAsync($"/api/friends/{userB}", userA);
        var showUntrustedEnvelope = await showUntrustedRes.ReadEnvelopeAsync<FriendshipReadModel>(HttpStatusCode.OK);
        await Assert.That(showUntrustedEnvelope.Data.Friendship.Level).IsEqualTo(FriendshipLevel.Friend);

        // 9. Delete Friendship
        using var deleteRes = await client.SendAuthedDeleteAsync($"/api/friends/{userB}", userA);
        await Assert.That(deleteRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // 10. Verify no longer friends
        using var listAfterDeleteRes = await client.SendAuthedGetAsync("/api/friends", userA);
        var listAfterDeleteEnvelope = await listAfterDeleteRes.ReadEnvelopeAsync<IReadOnlyList<FriendshipReadModel>>(HttpStatusCode.OK);
        await Assert.That(listAfterDeleteEnvelope.Data.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SelfActions_ReturnBadRequest()
    {
        using var client = fixture.Factory.CreateClient();
        var user = UniqueId("fr-self");
        await EnsureUserExistsAsync(client, user);

        // Show self
        using var showRes = await client.SendAuthedGetAsync($"/api/friends/{user}", user);
        await Assert.That(showRes.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var showError = await showRes.ReadErrorAsync(HttpStatusCode.BadRequest);
        await Assert.That(showError.Code).IsEqualTo(ErrorCodes.CannotViewOwnFriendship);

        // Delete self
        using var deleteRes = await client.SendAuthedDeleteAsync($"/api/friends/{user}", user);
        await Assert.That(deleteRes.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var deleteError = await deleteRes.ReadErrorAsync(HttpStatusCode.BadRequest);
        await Assert.That(deleteError.Code).IsEqualTo(ErrorCodes.CannotDeleteOwnFriendship);

        // Trust self
        using var trustRes = await client.SendAsJsonAsync(
            HttpMethod.Post, $"/api/friends/{user}/trust",
            new object(),
            user);
        await Assert.That(trustRes.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var trustError = await trustRes.ReadErrorAsync(HttpStatusCode.BadRequest);
        await Assert.That(trustError.Code).IsEqualTo(ErrorCodes.CannotTrustSelf);

        // Untrust self
        using var untrustRes = await client.SendAsJsonAsync(
            HttpMethod.Post, $"/api/friends/{user}/untrust",
            new object(),
            user);
        await Assert.That(untrustRes.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var untrustError = await untrustRes.ReadErrorAsync(HttpStatusCode.BadRequest);
        await Assert.That(untrustError.Code).IsEqualTo(ErrorCodes.CannotUntrustSelf);
    }
}