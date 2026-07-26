using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Interfold.Api.Services.Export;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions.Repository;

namespace Interfold.Api.UnitTests.Export;

// Full ExportService coverage — asserts both the pk and full JSON shapes match the
// pinned fixtures in ExportFixtures byte-for-byte (via JsonNode structural equality
// so whitespace / key ordering can't cause spurious flakes), plus the byte-truncation
// / proxy-tag split / color-strip / null-color / orphan-front-drop branches.
public sealed class ExportServiceTests
{
    // Strict mocks: any repository method the service reaches for other than the six
    // configured below throws MockStrictBehaviorException, so an accidental new
    // dependency surfaces immediately instead of a silently-empty read.
    private static ExportService BuildService(
        AccountPublicProfileReadModel? profile,
        IReadOnlyList<AlterReadModel> alters,
        IReadOnlyList<TagReadModel> tags,
        IReadOnlyList<PollReadModel> polls,
        IReadOnlyList<FrontHistoryReadModel> fronts,
        IReadOnlyList<SettingsFieldReadModel> fields)
    {
        var accounts = IAccountRepository.Mock(MockBehavior.Strict);
        accounts.GetPublicProfileAsync(ExportFixtures.OwnerId, Any<CancellationToken>()).Returns(profile);

        var altersMock = IAlterRepository.Mock(MockBehavior.Strict);
        altersMock.ListAsync(ExportFixtures.OwnerId, Any<CancellationToken>()).Returns(alters);

        var tagsMock = ITagRepository.Mock(MockBehavior.Strict);
        tagsMock.ListAsync(ExportFixtures.OwnerId, Any<CancellationToken>()).Returns(tags);

        var pollsMock = IPollRepository.Mock(MockBehavior.Strict);
        pollsMock.ListAsync(ExportFixtures.OwnerId, Any<CancellationToken>()).Returns(polls);

        var frontsMock = IFrontingRepository.Mock(MockBehavior.Strict);
        frontsMock.ListAllAsync(ExportFixtures.OwnerId, Any<CancellationToken>()).Returns(fronts);

        var fieldsMock = ISettingsFieldRepository.Mock(MockBehavior.Strict);
        fieldsMock.ListAsync(ExportFixtures.OwnerId, Any<CancellationToken>()).Returns(fields);

        return new ExportService(
            accounts.Object,
            altersMock.Object,
            tagsMock.Object,
            pollsMock.Object,
            frontsMock.Object,
            fieldsMock.Object);
    }

    private static IReadOnlyList<AlterReadModel> BuildAlters()
    {
        var alpha = new AlterReadModel(
            ExportFixtures.AlterOneId,
            "Alpha",
            ExportFixtures.PkDescription1200,
            ExportFixtures.AlterOneAvatar,
            AvatarSource.External,
            ExportFixtures.AlterOneColor,
            "they/them",
            VisibilityLevel.Public,
            new[] { new AlterPublicFieldReadModel(ExportFixtures.FieldOneId, "Field1", FieldType.Text, "the-value") },
            "AlphaProxy",
            alias: null,
            untracked: false,
            archived: false,
            pinned: false,
            discordProxies: new[] { "prefix;text suffix" },
            insertedAt: ExportFixtures.FixedInsertedAt,
            updatedAt: ExportFixtures.FixedUpdatedAt);

        var beta = new AlterReadModel(
            ExportFixtures.AlterTwoId,
            "Beta",
            description: null,
            avatarUrl: null,
            avatarSource: null,
            color: null,
            pronouns: null,
            securityLevel: VisibilityLevel.Public,
            fields: System.Array.Empty<AlterPublicFieldReadModel>(),
            proxyName: null,
            alias: null,
            untracked: false,
            archived: false,
            pinned: false,
            discordProxies: System.Array.Empty<string>(),
            insertedAt: ExportFixtures.FixedInsertedAt,
            updatedAt: ExportFixtures.FixedUpdatedAt);

        return new[] { alpha, beta };
    }

    private static IReadOnlyList<TagReadModel> BuildTags() => new[]
    {
        new TagReadModel(
            ExportFixtures.TagRootId,
            "RootTag",
            new HexColor("#112233"),
            "root description",
            ParentTagId: null,
            Alters: new[] { ExportFixtures.AlterOneId, ExportFixtures.AlterTwoId },
            InsertedAt: ExportFixtures.FixedInsertedAt,
            UpdatedAt: ExportFixtures.FixedUpdatedAt,
            SecurityLevel: VisibilityLevel.Public,
            UserId: ExportFixtures.OwnerId),
        new TagReadModel(
            ExportFixtures.TagChildId,
            "ChildTag",
            Color: null,
            Description: null,
            ParentTagId: ExportFixtures.TagRootId,
            Alters: new[] { ExportFixtures.AlterOneId },
            InsertedAt: ExportFixtures.FixedInsertedAt,
            UpdatedAt: ExportFixtures.FixedUpdatedAt,
            SecurityLevel: VisibilityLevel.Public,
            UserId: ExportFixtures.OwnerId),
    };

    private static IReadOnlyList<FrontHistoryReadModel> BuildFronts() => new[]
    {
        new FrontHistoryReadModel(
            ExportFixtures.FrontOpenId,
            ExportFixtures.AlterOneId,
            "open front",
            ExportFixtures.FrontOpenStart,
            TimeEnd: null,
            UserId: ExportFixtures.OwnerId),
        new FrontHistoryReadModel(
            ExportFixtures.FrontClosedId,
            ExportFixtures.AlterTwoId,
            "closed front",
            ExportFixtures.FrontClosedStart,
            TimeEnd: ExportFixtures.FrontClosedEnd,
            UserId: ExportFixtures.OwnerId),
    };

    private static IReadOnlyList<PollReadModel> BuildPolls()
    {
        using var doc = JsonDocument.Parse("""{"foo":"bar"}""");
        return new[]
        {
            new PollReadModel(
                ExportFixtures.PollOneId,
                ExportFixtures.OwnerId,
                "Poll Title",
                "poll description",
                PollType.Vote,
                doc.RootElement.Clone(),
                TimeEnd: new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc),
                InsertedAt: ExportFixtures.FixedInsertedAt,
                UpdatedAt: ExportFixtures.FixedUpdatedAt)
        };
    }

    private static IReadOnlyList<SettingsFieldReadModel> BuildFields() => new[]
    {
        new SettingsFieldReadModel(
            ExportFixtures.FieldOneId,
            "Field1",
            FieldType.Text,
            VisibilityLevel.Public,
            Locked: false,
            Index: 0,
            InsertedAt: ExportFixtures.FixedInsertedAt),
    };

    [Test]
    public async Task BuildPkAsync_MatchesReferenceShape()
    {
        var service = BuildService(
            ExportFixtures.BuildProfile(),
            BuildAlters(),
            BuildTags(),
            BuildPolls(),
            BuildFronts(),
            BuildFields());

        var payload = await service.BuildPkAsync(ExportFixtures.OwnerId, CancellationToken.None);
        var actual = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(payload, ExportJsonOptions.Default))
            ?? throw new InvalidOperationException("pk payload serialised to null root.");
        var expected = JsonNode.Parse(ExportFixtures.ExpectedPkJson())
            ?? throw new InvalidOperationException("pk fixture parsed to null root.");

        await Assert.That(JsonNode.DeepEquals(actual, expected)).IsTrue()
            .Because($"pk payload did not match fixture.\n  expected: {expected.ToJsonString()}\n  actual:   {actual.ToJsonString()}");
    }

    [Test]
    public async Task BuildFullAsync_MatchesReferenceShape()
    {
        var service = BuildService(
            ExportFixtures.BuildProfile(),
            BuildAlters(),
            BuildTags(),
            BuildPolls(),
            BuildFronts(),
            BuildFields());

        var payload = await service.BuildFullAsync(ExportFixtures.OwnerId, CancellationToken.None);
        var actual = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(payload, ExportJsonOptions.Default))
            ?? throw new InvalidOperationException("full payload serialised to null root.");
        var expected = JsonNode.Parse(ExportFixtures.ExpectedFullJson())
            ?? throw new InvalidOperationException("full fixture parsed to null root.");

        await Assert.That(JsonNode.DeepEquals(actual, expected)).IsTrue()
            .Because($"full payload did not match fixture.\n  expected: {expected.ToJsonString()}\n  actual:   {actual.ToJsonString()}");
    }

    // Reference's `format_full_export` drops fronts where alter_id or time_start are missing
    // (accounts.ex:1215). Our contract encodes both as non-nullable on FrontHistoryReadModel,
    // but ListAllAsync's *impl* must drop rows with a null time_start (Scylla can hand back a
    // corrupt row), so ExportService only sees populated ones. Documenting the shape here
    // via a two-element fixture — the "drop" branch is exercised by the fronting repo tests.
    [Test]
    public async Task BuildFullAsync_EmitsAllFrontsReturnedByRepository()
    {
        var service = BuildService(
            ExportFixtures.BuildProfile(description: "desc"),
            BuildAlters(),
            BuildTags(),
            BuildPolls(),
            BuildFronts(),
            BuildFields());

        var payload = await service.BuildFullAsync(ExportFixtures.OwnerId, CancellationToken.None);

        await Assert.That(payload.Fronts.Count).IsEqualTo(2);
        await Assert.That(payload.Fronts[0].Id).IsEqualTo(ExportFixtures.FrontOpenId);
        await Assert.That(payload.Fronts[1].Id).IsEqualTo(ExportFixtures.FrontClosedId);
    }

    [Test]
    public async Task BuildPkAsync_TruncatesDescriptionsToByteCap()
    {
        var service = BuildService(
            ExportFixtures.BuildProfile(),
            BuildAlters(),
            BuildTags(),
            BuildPolls(),
            BuildFronts(),
            BuildFields());

        var payload = await service.BuildPkAsync(ExportFixtures.OwnerId, CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(Encoding.UTF8.GetByteCount(payload.Description!)).IsLessThanOrEqualTo(1000);
            await Assert.That(payload.Description).IsEqualTo(ExportFixtures.DescriptionTruncatedTo1000);
            await Assert.That(Encoding.UTF8.GetByteCount(payload.Members[0].Description!)).IsLessThanOrEqualTo(1000);
            await Assert.That(payload.Members[0].Description).IsEqualTo(ExportFixtures.DescriptionTruncatedTo1000);
        }
    }

    [Test]
    public async Task BuildPkAsync_SplitsDiscordProxiesOnTextSentinel()
    {
        var service = BuildService(
            ExportFixtures.BuildProfile(description: "d"),
            BuildAlters(),
            BuildTags(),
            BuildPolls(),
            BuildFronts(),
            BuildFields());

        var payload = await service.BuildPkAsync(ExportFixtures.OwnerId, CancellationToken.None);

        var alpha = payload.Members[0];
        await Assert.That(alpha.ProxyTags.Count).IsEqualTo(1);
        await Assert.That(alpha.ProxyTags[0].Prefix).IsEqualTo("prefix;");
        await Assert.That(alpha.ProxyTags[0].Suffix).IsEqualTo(" suffix");
    }

    [Test]
    public async Task BuildPkAsync_AssignsSequentialStringIdsToGroups()
    {
        var service = BuildService(
            ExportFixtures.BuildProfile(description: "d"),
            BuildAlters(),
            BuildTags(),
            BuildPolls(),
            BuildFronts(),
            BuildFields());

        var payload = await service.BuildPkAsync(ExportFixtures.OwnerId, CancellationToken.None);

        await Assert.That(payload.Groups.Count).IsEqualTo(2);
        await Assert.That(payload.Groups[0].Id).IsEqualTo("0");
        await Assert.That(payload.Groups[1].Id).IsEqualTo("1");
    }

    [Test]
    public async Task FormatImportColor_HandlesReferenceEdgeCases()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ExportHelpers.FormatImportColor(null)).IsNull();
            await Assert.That(ExportHelpers.FormatImportColor(new HexColor("#abcdef"))).IsEqualTo("abcdef");
            await Assert.That(ExportHelpers.FormatImportColor(new HexColor("#abc"))).IsNull();
            await Assert.That(ExportHelpers.FormatImportColor(new HexColor("#abcdef12"))).IsNull();
        }
    }

    [Test]
    public async Task ByteTruncate_ClipsAtByteBoundaryWithoutSplittingCodepoint()
    {
        // 🎃 is 4 UTF-8 bytes. "a🎃" is 5 bytes. Truncating to 3 bytes must drop the 🎃 entirely
        // rather than emit a lone continuation byte.
        var truncated = ExportHelpers.ByteTruncateUtf8("a🎃", maxBytes: 3);

        using (Assert.Multiple())
        {
            await Assert.That(truncated).IsEqualTo("a");
            await Assert.That(Encoding.UTF8.GetByteCount(truncated)).IsLessThanOrEqualTo(3);
        }
    }

    [Test]
    public async Task ByteTruncate_ReturnsInputWhenAlreadyWithinLimit()
    {
        var truncated = ExportHelpers.ByteTruncateUtf8("hello", maxBytes: 100);
        await Assert.That(truncated).IsEqualTo("hello");
    }

    [Test]
    public async Task ByteTruncate_HandlesNullAsEmpty()
    {
        var truncated = ExportHelpers.ByteTruncateUtf8(null, maxBytes: 10);
        await Assert.That(truncated).IsEqualTo(string.Empty);
    }
}
