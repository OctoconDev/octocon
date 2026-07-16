using Interfold.Contracts.Ids;
using Interfold.Infrastructure.InMemory.Repository;

namespace Interfold.Api.UnitTests.Friendships;

/// <summary>
/// Pins the <see cref="FriendLookup"/>-driven dispatch matrix on
/// <see cref="InMemoryFriendshipRepository.ResolveUserIdAsync"/>. The Scylla dispatch
/// path exercises the same routing table, but the integration-level
/// <c>SendFriendRequestPrefixTests</c> covers that end-to-end — running the Scylla
/// dispatch through unit tests would require a fake CQL session which just re-implements
/// the same switch we're already testing here.
///
/// <para>
/// The pre-merge four-kind universe (Region / Username / Discord / Id) has been
/// narrowed to two kinds at the wire boundary; the Discord dispatch branch (and its
/// associated account-repo delegation) is deleted along with the shapes it consumed.
/// Non-id/username shapes now fail <see cref="FriendLookup.TryParse"/> and surface as a
/// 400 at ASP.NET route binding — see <c>FriendLookupTests</c> for the type-level pin
/// and <c>SendFriendRequestPrefixTests</c> for the end-to-end 400 assertion.
/// </para>
/// </summary>
public sealed class ResolveUserIdDispatchTests
{
    // ---------------- Username dispatch (InMemory has no reverse index) -----

    [Test]
    public async Task FriendLookup_UsernamePrefix_ReturnsNull_NoRegistryHop()
    {
        // InMemory has no users_by_username table. Returning SystemId("username:alice")
        // verbatim (treating the literal string as a system id) is the exact footgun
        // FriendLookup + the switch-on-Kind dispatch prevent.
        var repo = new InMemoryFriendshipRepository();

        var resolved = await repo.ResolveUserIdAsync(FriendLookup.Parse("username:alice", provider: null));

        await Assert.That(resolved).IsNull()
            .Because("Username lookup has no InMemory reverse index; the correct answer is 'no such user' rather than fabricating a SystemId('username:alice').");
    }

    // ---------------- Id dispatch (bare + explicit id: prefix) --------------

    [Test]
    [Arguments("id:abcdefg", "abcdefg")]
    [Arguments("abcdefg",    "abcdefg")]
    public async Task FriendLookup_IdShapes_NormalizeToValue(string input, string expected)
    {
        // Both shapes parse as FriendLookupKind.Id and dispatch through the "normalise
        // the after-prefix Value and return it as the storage key" branch. Region-scoped
        // (nam:...) and unknown-prefix inputs no longer reach here — they fail
        // FriendLookup.TryParse at route binding.
        var repo = new InMemoryFriendshipRepository();

        var resolved = await repo.ResolveUserIdAsync(FriendLookup.Parse(input, provider: null));

        await Assert.That(resolved?.Value).IsEqualTo(expected)
            .Because($"'{input}' must normalise to '{expected}' so the resolved id can be used as a storage key for the other InMemory repos (which write with region-stripped keys).");
    }
}
