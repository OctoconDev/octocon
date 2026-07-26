using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.IntegrationTests.Services.Scylla;

public sealed class ScyllaMappingRegressionTests : BaseEndpointTest
{
    [Test]
    public async Task PollUuidParsing_AcceptsCompactAndHyphenated_RejectsInvalid()
    {
        var compact = Guid.NewGuid().ToString("N");
        var hyphenated = Guid.NewGuid().ToString("D");

        var compactOk = PollId.TryParse(compact, provider: null, out var compactParsed);
        var hyphenatedOk = PollId.TryParse(hyphenated, provider: null, out var hyphenatedParsed);
        var invalidOk = PollId.TryParse("not-a-guid", provider: null, out _);

        using (Assert.Multiple())
        {
            await Assert.That(compactOk).IsTrue();
            await Assert.That(hyphenatedOk).IsTrue();
            await Assert.That(invalidOk).IsFalse();
            await Assert.That(compactParsed.Value.ToString("N")).IsEqualTo(compact);
            await Assert.That(hyphenatedParsed.Value.ToString("D")).IsEqualTo(hyphenated);
        }
    }

    [Test]
    public async Task PollTypeMapping_KnownCodesMapExactly_UnknownCodesThrow()
    {
        // Pins the Option D 2026-07-17 strict-throw flip: every repo call site now uses
        // the fallback-less `.FromCode<PollType>()` overload, so unknown on-disk shorts
        // must surface as ArgumentOutOfRangeException instead of silently coercing to Vote.
        // The pre-flip lenient version of this test asserted `.IsEqualTo(PollType.Vote)`
        // for MinValue/MaxValue — that behaviour is intentionally dead.
        using (Assert.Multiple())
        {
            await Assert.That((short)PollType.Vote).IsEqualTo((short)0);
            await Assert.That((short)PollType.Choice).IsEqualTo((short)1);
            await Assert.That((short)PollType.Approval).IsEqualTo((short)2);

            await Assert.That(((short)0).FromCode<PollType>()).IsEqualTo(PollType.Vote);
            await Assert.That(((short)1).FromCode<PollType>()).IsEqualTo(PollType.Choice);
            await Assert.That(((short)2).FromCode<PollType>()).IsEqualTo(PollType.Approval);

            await Assert.That(() => short.MinValue.FromCode<PollType>())
                .Throws<ArgumentOutOfRangeException>()
                .Because("Unknown on-disk PollType shorts must fail loud so corrupt/stale rows surface at the read site.");
            await Assert.That(() => short.MaxValue.FromCode<PollType>())
                .Throws<ArgumentOutOfRangeException>()
                .Because("Same strict contract as MinValue — no silent coercion to the default enum member.");
        }
    }

    [Test]
    public async Task FriendshipLevelMapping_KnownCodesMapExactly_UnknownCodesThrow()
    {
        // Same strict-throw contract as PollType. Silently downgrading a corrupt
        // trusted_friend row to Friend was the previous fallback — that hides a
        // security-relevant miscoercion from operators, so the strict flip is the
        // right default.
        using (Assert.Multiple())
        {
            await Assert.That(((short)0).FromCode<FriendshipLevel>()).IsEqualTo(FriendshipLevel.Friend);
            await Assert.That(((short)1).FromCode<FriendshipLevel>()).IsEqualTo(FriendshipLevel.TrustedFriend);

            await Assert.That(() => short.MinValue.FromCode<FriendshipLevel>())
                .Throws<ArgumentOutOfRangeException>()
                .Because("Unknown on-disk FriendshipLevel shorts must fail loud rather than downgrading to Friend.");
            await Assert.That(() => short.MaxValue.FromCode<FriendshipLevel>())
                .Throws<ArgumentOutOfRangeException>()
                .Because("Same strict contract as MinValue.");
        }
    }

    [Test]
    public async Task UuidParsingRegression_TagJournalSettings_AcceptsCompactAndRejectsInvalid()
    {
        var tag = Guid.NewGuid().ToString("N");
        var journal = Guid.NewGuid().ToString("N");
        var field = Guid.NewGuid().ToString("N");

        using (Assert.Multiple())
        {
            await Assert.That(TagId.TryParse(tag, provider: null, out _)).IsTrue();
            await Assert.That(EntryId.TryParse(journal, provider: null, out _)).IsTrue();
            await Assert.That(FieldId.TryParse(field, provider: null, out _)).IsTrue();

            await Assert.That(TagId.TryParse("bad", provider: null, out _)).IsFalse();
            await Assert.That(EntryId.TryParse("bad", provider: null, out _)).IsFalse();
            await Assert.That(FieldId.TryParse("bad", provider: null, out _)).IsFalse();
        }
    }
}