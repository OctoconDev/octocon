using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

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
    public async Task PollTypeMapping_HandlesKnownAndBoundaryValues()
    {
        using (Assert.Multiple())
        {
            await Assert.That((short)PollType.Vote).IsEqualTo((short)0);
            await Assert.That((short)PollType.Choice).IsEqualTo((short)1);
            await Assert.That((short)PollType.Approval).IsEqualTo((short)2);

            await Assert.That(((short)0).FromCode(PollType.Vote)).IsEqualTo(PollType.Vote);
            await Assert.That(((short)1).FromCode(PollType.Vote)).IsEqualTo(PollType.Choice);
            await Assert.That(((short)2).FromCode(PollType.Vote)).IsEqualTo(PollType.Approval);
            await Assert.That(short.MinValue.FromCode(PollType.Vote)).IsEqualTo(PollType.Vote);
            await Assert.That(short.MaxValue.FromCode(PollType.Vote)).IsEqualTo(PollType.Vote);
        }
    }

    [Test]
    public async Task FriendshipLevelMapping_HandlesKnownAndBoundaryValues()
    {
        using (Assert.Multiple())
        {
            await Assert.That(((short)0).FromCode(FriendshipLevel.Friend)).IsEqualTo(FriendshipLevel.Friend);
            await Assert.That(((short)1).FromCode(FriendshipLevel.Friend)).IsEqualTo(FriendshipLevel.TrustedFriend);
            await Assert.That(short.MinValue.FromCode(FriendshipLevel.Friend)).IsEqualTo(FriendshipLevel.Friend);
            await Assert.That(short.MaxValue.FromCode(FriendshipLevel.Friend)).IsEqualTo(FriendshipLevel.Friend);
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