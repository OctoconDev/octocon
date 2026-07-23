using Interfold.Shared.Contracts.Ids;
using Interfold.Infrastructure.InMemory.Repository;

namespace Interfold.Api.UnitTests.Friendships;

// FriendLookup-driven dispatch matrix on InMemoryFriendshipRepository.ResolveUserIdAsync.
// Scylla dispatch runs through SendFriendRequestPrefixTests end-to-end. Wire universe is
// narrowed to Id + Username; other shapes fail FriendLookup.TryParse (400 at route bind).
public sealed class ResolveUserIdDispatchTests
{
    [Test]
    public async Task FriendLookup_UsernamePrefix_ReturnsNull_NoRegistryHop()
    {
        var repo = new InMemoryFriendshipRepository();

        var resolved = await repo.ResolveUserIdAsync(FriendLookup.Parse("username:alice", provider: null));

        await Assert.That(resolved).IsNull()
            .Because("Username lookup has no InMemory reverse index; the correct answer is 'no such user' rather than fabricating a SystemId('username:alice').");
    }

    [Test]
    [Arguments("id:abcdefg", "abcdefg")]
    [Arguments("abcdefg",    "abcdefg")]
    public async Task FriendLookup_IdShapes_NormalizeToValue(string input, string expected)
    {
        var repo = new InMemoryFriendshipRepository();

        var resolved = await repo.ResolveUserIdAsync(FriendLookup.Parse(input, provider: null));

        await Assert.That(resolved?.Value).IsEqualTo(expected)
            .Because($"'{input}' must normalise to '{expected}' so the resolved id can be used as a storage key for the other InMemory repos (which write with region-stripped keys).");
    }
}
