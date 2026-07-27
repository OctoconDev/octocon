using Interfold.Friendships.Contracts.Ids;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

// Pins the FriendLookupExtensions.RepresentsSameUserAs extension that backs the
// FriendRequestsController.Send self-request guard — moved off ScopedSystemId when
// FriendLookup relocated to Interfold.Friendships.Contracts.
public sealed class FriendLookupRepresentsSameUserAsTests
{
    private static readonly ScopedSystemId Principal = ScopedSystemId.Compose(ScyllaKeyspace.Nam, "abcdefg");

    // Bare and id:-prefixed both parse as FriendLookupKind.Id and delegate through the
    // SystemId primitive's raw-id branch.
    [Test]
    [Arguments("abcdefg")]
    [Arguments("id:abcdefg")]
    public async Task FriendLookup_IdSelf_IsSelf(string input)
    {
        var candidate = FriendLookup.Parse(input, provider: null);

        await Assert.That(candidate.RepresentsSameUserAs(Principal)).IsTrue()
            .Because($"'{input}' parses as FriendLookupKind.Id whose Value equals the principal's RawId; the extension must delegate to the SystemId primitive's raw-id branch and self-reject.");
    }

    // Username shape can't self-reject without a registry hit; the downstream
    // resolved-id self-check takes over.
    [Test]
    public async Task FriendLookup_UsernameShape_IsNotSelf()
    {
        var candidate = FriendLookup.Parse("username:alice", provider: null);

        await Assert.That(candidate.RepresentsSameUserAs(Principal)).IsFalse()
            .Because("The controller cannot decide 'is alice me?' without a registry lookup; the fast-path must return false and let the downstream resolved-id self-check take over.");
    }
}
