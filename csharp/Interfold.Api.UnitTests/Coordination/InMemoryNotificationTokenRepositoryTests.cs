using Interfold.Infrastructure.InMemory.Repository;
using Interfold.Contracts.Ids;

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

    [Test]
    public async Task ListTokensForFriendsOf_OmitsFriendsWithoutTokens()
    {
        // Bob is Alice's friend but never registered a push token. The grouped shape
        // must skip him entirely (rather than returning a group with an empty Tokens
        // list) so callers don't have to guard on group.Tokens.Count > 0.
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
        // A single friend on multiple devices (phone + tablet, or Android + iOS) has
        // multiple registered tokens. They collapse into one FriendNotificationTokens
        // entry so the FCM sender can batch them under a single per-friend message.
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
        // Defensive: an empty systemId should return an empty list rather than crash.
        // The FCMService entrypoint short-circuits on this too, but the repository
        // shouldn't rely on the caller doing so.
        var friendships = new InMemoryFriendshipRepository();
        var tokens = new InMemoryNotificationTokenRepository(friendships);

        var result = await tokens.ListTokensForFriendsOfAsync(new(""), CancellationToken.None);
        await Assert.That(result.Count).IsEqualTo(0);
    }
}
