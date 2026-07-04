using Interfold.Infrastructure.InMemory.Repository;

namespace Interfold.Api.UnitTests.Coordination;

/// <summary>
/// Contract tests for <see cref="InMemoryNotificationTokenRepository.ListTokensForFriendsOfAsync"/>
/// — the repository method <see cref="Interfold.Infrastructure.Coordination.FirebaseFCMService"/>
/// leans on to resolve recipients for a fronting-changed notification. The in-memory
/// backend is what integration tests and the local dev loop run against, so its shape
/// contract must match the Scylla implementation (groups by friend, drops friends with
/// no tokens) or tests will diverge silently between backends.
/// </summary>
public sealed class InMemoryNotificationTokenRepositoryTests
{
    [Test]
    public async Task ListTokensForFriendsOf_GroupsByFriend_ExcludingCallerOwn()
    {
        var friendships = new InMemoryFriendshipRepository();
        var tokens = new InMemoryNotificationTokenRepository(friendships);

        // Alice registers a token for herself; Bob and Carol are her friends and each
        // register their own tokens. A push to Alice's friends should hit Bob and Carol,
        // not Alice — the whole point of the fronting-change flow is to notify others.
        await tokens.AddAsync("alice", "alice-token", CancellationToken.None);
        await tokens.AddAsync("bob", "bob-token", CancellationToken.None);
        await tokens.AddAsync("carol", "carol-token", CancellationToken.None);

        await friendships.SendRequestAsync("alice", "bob");
        await friendships.AcceptRequestAsync("bob", "alice");
        await friendships.SendRequestAsync("alice", "carol");
        await friendships.AcceptRequestAsync("carol", "alice");

        var result = await tokens.ListTokensForFriendsOfAsync("alice", CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result.Any(g => g.FriendSystemId == "bob" && g.Tokens.Contains("bob-token"))).IsTrue();
        await Assert.That(result.Any(g => g.FriendSystemId == "carol" && g.Tokens.Contains("carol-token"))).IsTrue();
        await Assert.That(result.Any(g => g.FriendSystemId == "alice")).IsFalse()
            .Because("The fronting-changed push targets friends of the system, not the system itself.");
    }

    [Test]
    public async Task ListTokensForFriendsOf_ReturnsEmptyForSystemWithNoFriends()
    {
        var friendships = new InMemoryFriendshipRepository();
        var tokens = new InMemoryNotificationTokenRepository(friendships);
        await tokens.AddAsync("lonely", "lonely-token", CancellationToken.None);

        var result = await tokens.ListTokensForFriendsOfAsync("lonely", CancellationToken.None);
        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ListTokensForFriendsOf_OmitsFriendsWithoutTokens()
    {
        // Bob is Alice's friend but never registered a push token. The grouped shape
        // must skip him entirely (rather than returning a group with an empty Tokens
        // list) so callers don't have to guard on group.Tokens.Count > 0.
        var friendships = new InMemoryFriendshipRepository();
        var tokens = new InMemoryNotificationTokenRepository(friendships);

        await friendships.SendRequestAsync("alice", "bob");
        await friendships.AcceptRequestAsync("bob", "alice");

        var result = await tokens.ListTokensForFriendsOfAsync("alice", CancellationToken.None);
        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ListTokensForFriendsOf_MergesMultipleTokensPerFriend()
    {
        // A single friend on multiple devices (phone + tablet, or Android + iOS) has
        // multiple registered tokens. They collapse into one FriendNotificationTokens
        // entry so the FCM sender can batch them under a single per-friend message.
        var friendships = new InMemoryFriendshipRepository();
        var tokens = new InMemoryNotificationTokenRepository(friendships);
        await tokens.AddAsync("bob", "bob-phone", CancellationToken.None);
        await tokens.AddAsync("bob", "bob-tablet", CancellationToken.None);

        await friendships.SendRequestAsync("alice", "bob");
        await friendships.AcceptRequestAsync("bob", "alice");

        var result = await tokens.ListTokensForFriendsOfAsync("alice", CancellationToken.None);
        await Assert.That(result.Count).IsEqualTo(1);

        var bobGroup = result.Single();
        await Assert.That(bobGroup.FriendSystemId).IsEqualTo("bob");
        await Assert.That(bobGroup.Tokens.Count).IsEqualTo(2);
        await Assert.That(bobGroup.Tokens.Contains("bob-phone")).IsTrue();
        await Assert.That(bobGroup.Tokens.Contains("bob-tablet")).IsTrue();
    }

    [Test]
    public async Task ListTokensForFriendsOf_HandlesBlankSystemIdGracefully()
    {
        // Defensive: an empty systemId should return an empty list rather than crash.
        // The FCMService entrypoint short-circuits on this too, but the repository
        // shouldn't rely on the caller doing so.
        var friendships = new InMemoryFriendshipRepository();
        var tokens = new InMemoryNotificationTokenRepository(friendships);

        var result = await tokens.ListTokensForFriendsOfAsync("", CancellationToken.None);
        await Assert.That(result.Count).IsEqualTo(0);
    }
}
