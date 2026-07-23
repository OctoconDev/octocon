using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

// Pins the RepresentsSameUserAs primitives that back the eight controller self-request
// guards. A bare byte compare would miss the raw-id shape (/api/friends/{rawId}); these
// tests catch that regression.
public sealed class ScopedSystemIdRepresentsSameUserAsTests
{
    private static readonly ScopedSystemId Principal = ScopedSystemId.Compose(ScyllaKeyspace.Nam, "abcdefg");

    [Test]
    public async Task SystemId_SameRegionScoped_IsSelf()
    {
        SystemId candidate = new("nam:abcdefg");

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsTrue()
            .Because("A scoped candidate in the principal's region and with the principal's raw id is the same user.");
    }

    // A raw byte compare would see "nam:abcdefg" == "abcdefg" → false; the semantic
    // check catches it up front.
    [Test]
    public async Task SystemId_RawId_MatchingPrincipalRawId_IsSelf()
    {
        SystemId candidate = new("abcdefg");

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsTrue()
            .Because("A raw candidate whose value equals the principal's RawId represents the same user in the principal's region.");
    }

    // Cross-region scoped is a different user per the "scoped composite is identity"
    // contract; must not coerce to the principal's region.
    [Test]
    public async Task SystemId_CrossRegionScopedWithSameRawId_IsNotSelf()
    {
        SystemId candidate = new("eur:abcdefg");

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsFalse()
            .Because("A cross-region scoped id with the same raw id is a different user per the 'scoped composite is identity' contract — the self-guard must not coerce it into the principal's region.");
    }

    [Test]
    public async Task SystemId_SameRegionDifferentRawId_IsNotSelf()
    {
        SystemId candidate = new("nam:xyzzyxq");

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsFalse()
            .Because("Same-region scoped candidates with a different raw id are different users.");
    }

    // Blank inputs must not blow up defensive callers (test fixtures, migration scripts).
    [Test]
    public async Task SystemId_BlankValue_ReturnsFalse()
    {
        SystemId candidate = new(string.Empty);

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsFalse()
            .Because("Blank candidates cannot represent any user — the primitive must return false rather than throw so the guard is safe on defensive inputs.");
    }

    // Bare and id:-prefixed both parse as FriendLookupKind.Id and delegate through the
    // SystemId primitive's raw-id branch.
    [Test]
    [Arguments("abcdefg")]
    [Arguments("id:abcdefg")]
    public async Task FriendLookup_IdSelf_IsSelf(string input)
    {
        var candidate = FriendLookup.Parse(input, provider: null);

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsTrue()
            .Because($"'{input}' parses as FriendLookupKind.Id whose Value equals the principal's RawId; the overload must delegate to the SystemId primitive's raw-id branch and self-reject.");
    }

    // Username shape can't self-reject without a registry hit; the downstream
    // resolved-id self-check takes over.
    [Test]
    public async Task FriendLookup_UsernameShape_IsNotSelf()
    {
        var candidate = FriendLookup.Parse("username:alice", provider: null);

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsFalse()
            .Because("The controller cannot decide 'is alice me?' without a registry lookup; the fast-path must return false and let the downstream resolved-id self-check take over.");
    }
}
