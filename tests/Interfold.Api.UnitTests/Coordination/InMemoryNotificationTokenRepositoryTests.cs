using Interfold.Infrastructure.InMemory.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Coordination;

// Contract for InMemoryNotificationTokenRepository.ListTokensForFriendsOfAsync — the
// InMemory backend must match Scylla (grouped by friend, friends without tokens dropped)
// or integration tests diverge silently between backends.
public sealed class InMemoryNotificationTokenRepositoryTests
{
    [Test]
    public async Task ListTokensForFriendsOf_GroupsByFriend_ExcludingCallerOwn()
    {
        var friendships = new InMemoryFriendshipRepository();
        var tokens = new InMemoryNotificationTokenRepository(friendships);

        await tokens.AddAsync(new("alice"), new("alice-token"), CancellationToken.None);
        await tokens.AddAsync(new("bob"), new("bob-token"), CancellationToken.None);
        await tokens.AddAsync(new("carol"), new("carol-token"), CancellationToken.None);

        await friendships.SendRequestAsync(new("alice"), new("bob"));
        await friendships.AcceptRequestAsync(new("bob"), new("alice"));
        await friendships.SendRequestAsync(new("alice"), new("carol"));
        await friendships.AcceptRequestAsync(new("carol"), new("alice"));

        var result = await tokens.ListTokensForFriendsOfAsync(new("alice"), CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result.Any(g => g.FriendSystemId == new SystemId("bob") && g.Tokens.Contains(new PushToken("bob-token")))).IsTrue();
        await Assert.That(result.Any(g => g.FriendSystemId == new SystemId("carol") && g.Tokens.Contains(new PushToken("carol-token")))).IsTrue();
        await Assert.That(result.Any(g => g.FriendSystemId == new SystemId("alice"))).IsFalse()
            .Because("The fronting-changed push targets friends of the system, not the system itself.");
    }

    [Test]
    public async Task ListTokensForFriendsOf_ReturnsEmptyForSystemWithNoFriends()
    {
        var friendships = new InMemoryFriendshipRepository();
        var tokens = new InMemoryNotificationTokenRepository(friendships);
        await tokens.AddAsync(new("lonely"), new("lonely-token"), CancellationToken.None);

        var result = await tokens.ListTokensForFriendsOfAsync(new("lonely"), CancellationToken.None);
        await Assert.That(result.Count).IsEqualTo(0);
    }

    // Friends without tokens are omitted entirely (not returned as empty groups) so
    // callers don't have to guard on Tokens.Count > 0.
    [Test]
    public async Task ListTokensForFriendsOf_OmitsFriendsWithoutTokens()
    {
        var friendships = new InMemoryFriendshipRepository();
        var tokens = new InMemoryNotificationTokenRepository(friendships);

        await friendships.SendRequestAsync(new("alice"), new("bob"));
        await friendships.AcceptRequestAsync(new("bob"), new("alice"));

        var result = await tokens.ListTokensForFriendsOfAsync(new("alice"), CancellationToken.None);
        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ListTokensForFriendsOf_MergesMultipleTokensPerFriend()
    {
        var friendships = new InMemoryFriendshipRepository();
        var tokens = new InMemoryNotificationTokenRepository(friendships);
        await tokens.AddAsync(new("bob"), new("bob-phone"), CancellationToken.None);
        await tokens.AddAsync(new("bob"), new("bob-tablet"), CancellationToken.None);

        await friendships.SendRequestAsync(new("alice"), new("bob"));
        await friendships.AcceptRequestAsync(new("bob"), new("alice"));

        var result = await tokens.ListTokensForFriendsOfAsync(new("alice"), CancellationToken.None);
        await Assert.That(result.Count).IsEqualTo(1);

        var bobGroup = result.Single();
        await Assert.That(bobGroup.FriendSystemId).IsEqualTo(new SystemId("bob"));
        await Assert.That(bobGroup.Tokens.Count).IsEqualTo(2);
        await Assert.That(bobGroup.Tokens.Contains(new PushToken("bob-phone"))).IsTrue();
        await Assert.That(bobGroup.Tokens.Contains(new PushToken("bob-tablet"))).IsTrue();
    }

    [Test]
    public async Task ListTokensForFriendsOf_HandlesBlankSystemIdGracefully()
    {
        var friendships = new InMemoryFriendshipRepository();
        var tokens = new InMemoryNotificationTokenRepository(friendships);

        var result = await tokens.ListTokensForFriendsOfAsync(new(""), CancellationToken.None);
        await Assert.That(result.Count).IsEqualTo(0);
    }
}
