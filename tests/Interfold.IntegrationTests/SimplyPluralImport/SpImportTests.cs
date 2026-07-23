using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Web;
using Interfold.Api.Services;
using Interfold.Api.SimplyPlural;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.ImportJobs;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.InMemory;
using Interfold.IntegrationTests.TestServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.IntegrationTests.SimplyPluralImport;

/// <summary>
/// Regression coverage for the Simply Plural import "today-date" bug fix. Every test wires up the
/// real <see cref="SimplyPluralImportService"/> against the InMemory persistence stack and feeds
/// it canned SP responses through <see cref="TestServices.StubSpHandler"/>, so we keep coverage
/// after Apparyllis sunsets the live API.
///
/// Data hygiene: every test mints fresh identifiers via <see cref="Uid"/> / <see cref="MemberUuid"/>
/// and uses obviously-synthetic timestamps (never the real values from the production dry-run
/// captures). No real account data lives in this file.
/// </summary>
public sealed class SpImportTests : BaseEndpointTest
{
    private static SystemId Uid() => TestIds.NewTypedSystemId("sys", maxLen: 16);
    private static string MemberUuid() => Guid.NewGuid().ToString("N");

    // Synthetic timestamps (ms since Unix epoch). The whole point of this test file is to
    // verify the import respects these dates *exactly* and never silently substitutes today.
    private const long Date_2020_01_15 = 1579082400000;     // 2020-01-15T10:00:00Z
    private const long Date_2021_06_10 = 1623333600000;     // 2021-06-10T14:00:00Z
    private const long Date_2021_06_10_End = 1623337200000; // 2021-06-10T15:00:00Z
    private const long Date_2022_03_05 = 1646474400000;     // 2022-03-05T10:00:00Z

    [Test]
    public async Task LiveFronter_WithRealStartTime_PreservesPastDate()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        var frontId = MemberUuid();

        // Sticky live front with a real past startTime — must be preserved verbatim.
        var liveFronters = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = frontId,
                content = new { member = alpha, startTime = Date_2020_01_15, live = true },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, currentFrontersJson: liveFronters);

        var (sp, frontingRepo, _, _, _, _) = await RunImportAsync(stub, systemId);
        await Assert.That(sp).IsNotNull();

        var active = await frontingRepo.ListActiveAsync(systemId);
        await Assert.That(active.Count).IsEqualTo(1);

        var expected = DateTimeOffset.FromUnixTimeMilliseconds(Date_2020_01_15);
        await Assert.That(active[0].Front.TimeStart).IsEqualTo(expected);

        // Defensive: an accidental DateTimeOffset.UtcNow substitution would land within
        // a few seconds of "now"; the synthetic date is years in the past.
        await Assert.That(active[0].Front.TimeStart)
            .IsLessThan(DateTimeOffset.UtcNow.AddDays(-30));
    }

    [Test]
    public async Task LiveFronter_StartTimeZero_FallsBackToObjectId()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();

        // SP omits startTime on some sticky/primary live fronts. The importer now decodes
        // the front document's ObjectId for its creation second instead of falling back to
        // lastOperationTime (which drifts on every edit). Golden 24-hex id 0x671425cd ->
        // 2024-10-19 21:34:05Z. We set lastOperationTime to a deliberately different date
        // so this test fails loudly if any future regression starts reading it again.
        const string spFrontIdHistorical = "671425cd02c63d13c59ba474";
        var expectedStart = new DateTimeOffset(2024, 10, 19, 21, 34, 5, TimeSpan.Zero);

        var liveFronters = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spFrontIdHistorical,
                content = new
                {
                    member = alpha,
                    startTime = 0L,
                    lastOperationTime = Date_2021_06_10,
                    live = true,
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, currentFrontersJson: liveFronters);

        var (_, frontingRepo, _, _, _, _) = await RunImportAsync(stub, systemId);

        var active = await frontingRepo.ListActiveAsync(systemId);
        await Assert.That(active.Count).IsEqualTo(1);

        using (Assert.Multiple())
        {
            await Assert.That(active[0].Front.TimeStart).IsEqualTo(expectedStart);
            // ObjectId wins over lastOperationTime: the 2021-06-10 value must NOT be used.
            await Assert.That(active[0].Front.TimeStart)
                .IsNotEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(Date_2021_06_10));
            await Assert.That(active[0].Front.TimeStart)
                .IsLessThan(DateTimeOffset.UtcNow.AddDays(-30));
        }
    }

    [Test]
    public async Task LiveFronter_BothZero_SkippedWithWarning()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        var frontId = MemberUuid();

        // Both startTime and lastOperationTime missing — must be skipped, never stamped today.
        var liveFronters = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = frontId,
                content = new
                {
                    member = alpha,
                    startTime = 0L,
                    lastOperationTime = 0L,
                    live = true,
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, currentFrontersJson: liveFronters);

        var (_, frontingRepo, _, _, _, logger) = await RunImportAsync(stub, systemId);

        var active = await frontingRepo.ListActiveAsync(systemId);
        await Assert.That(active.Count).IsEqualTo(0);

        var warnings = logger.Records
            .Where(r => r.Level == LogLevel.Warning && r.Message.Contains("Skipping live SP fronter"))
            .ToArray();
        using (Assert.Multiple())
        {
            await Assert.That(warnings.Length).IsEqualTo(1);
            // Confirm the warning mentions the synthetic SP member uuid so the operator
            // can trace which row was skipped without enabling debug logging.
            await Assert.That(warnings[0].Message).Contains(alpha);
        }
    }

    [Test]
    public async Task HistoricalFront_StartAndEnd_RetainOriginalTimes()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        var historicalFrontId = MemberUuid();

        // Closed historical front in a single past chunk. ImportFrontsAsync sends one request
        // per chunk window with startTime/endTime query params; OnGetPrefix lets the same body
        // serve every chunk request. Each chunk returning the same payload is fine because
        // ImportFrontsAsync dedupes by SpEntity.Id.
        var historyJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = historicalFrontId,
                content = new
                {
                    member = alpha,
                    startTime = Date_2021_06_10,
                    endTime = Date_2021_06_10_End,
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, frontHistoryJson: historyJson, currentFrontersJson: "[]");

        var (_, frontingRepo, _, _, _, _) = await RunImportAsync(stub, systemId);

        var history = await frontingRepo.ListHistoryBetweenAsync(
            systemId,
            DateTimeOffset.FromUnixTimeMilliseconds(0),
            DateTimeOffset.UtcNow.AddYears(1));

        await Assert.That(history.Count).IsEqualTo(1);
        var entry = history[0];
        using (Assert.Multiple())
        {
            await Assert.That(entry.TimeStart).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(Date_2021_06_10));
            await Assert.That(entry.TimeEnd).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(Date_2021_06_10_End));
        }
    }

    [Test]
    public async Task ChunkLoop_IssuesExactlyNumberOfChunksRequests_NoOffByOne()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();

        var stub = BuildSp(sysId.Value, alpha,
            frontHistoryJson: "[]",
            currentFrontersJson: "[]");

        await RunImportAsync(stub, systemId);

        // ImportFrontsAsync chunks the SP epoch (2015-01-01) → now in 6-month windows.
        // Pre-fix the loop ran `i <= numberOfChunks`, issuing one extra request that
        // always returned `[]`. Compute the expected count with the same formula the
        // production code uses so the assertion stays correct as time marches on.
        const long startEpoch = 1_420_070_400_000;
        const int monthInterval = 6;
        var chunkSizeMs = (long)monthInterval * 30 * 24 * 60 * 60 * 1000;
        var endTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expectedChunks = (int)Math.Ceiling((double)(endTimeMs - startEpoch) / chunkSizeMs);

        var historyRequests = stub.RequestsTo($"/v1/frontHistory/{sysId}");
        await Assert.That(historyRequests.Count).IsEqualTo(expectedChunks);

        // Sanity-check: no captured chunk should have startTime > endTime (the off-by-one
        // bug produced exactly that on its final iteration).
        foreach (var url in historyRequests)
        {
            var queryIndex = url.IndexOf('?');
            await Assert.That(queryIndex).IsGreaterThan(-1);
            var query = HttpUtility.ParseQueryString(url[(queryIndex + 1)..]);
            var startMs = long.Parse(query["startTime"]!, CultureInfo.InvariantCulture);
            var endMs = long.Parse(query["endTime"]!, CultureInfo.InvariantCulture);
            await Assert.That(startMs).IsLessThanOrEqualTo(endMs);
        }
    }

    [Test]
    public async Task Poll_TitleAndDescriptionTooLong_TruncatedToHandlerLimits()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();

        // SP has no length limits server-side; Interfold's CreatePollCommandHandler enforces
        // title <= 100 and desc <= 2000. The import path bypasses the handler and previously
        // truncated to 200/3000, leaving rows that the public API would reject on next edit.
        var longTitle = new string('t', 250);
        var longDesc = new string('d', 4000);
        var pollsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = MemberUuid(),
                content = new
                {
                    name = longTitle,
                    desc = longDesc,
                    custom = false,
                    endTime = 0L,
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, pollsJson: pollsJson);
        var (_, _, pollRepo, _, _, _) = await RunImportAsync(stub, systemId);

        var polls = await pollRepo.ListAsync(systemId);
        await Assert.That(polls.Count).IsEqualTo(1);
        await Assert.That(polls[0].Title.Length).IsEqualTo(100);
        await Assert.That(polls[0].Description).IsNotNull();
        await Assert.That(polls[0].Description!.Length).IsEqualTo(2000);
    }

    [Test]
    public async Task CustomPoll_WithEmptyOptionsArray_IsSkippedAndWarned()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        var spPollId = MemberUuid();

        // SP's v1 schema marks `options` as optional. Importing a custom poll with no options
        // would create a "choice" row that users can't vote on, so the importer should bail.
        var pollsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spPollId,
                content = new
                {
                    name = "synthetic choice",
                    desc = "",
                    custom = true,
                    endTime = 0L,
                    options = Array.Empty<object>(),
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, pollsJson: pollsJson);
        var (_, _, pollRepo, _, _, logger) = await RunImportAsync(stub, systemId);

        var polls = await pollRepo.ListAsync(systemId);
        await Assert.That(polls.Count).IsEqualTo(0);

        var warned = logger.Records.Any(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains(spPollId, StringComparison.Ordinal) &&
            r.Message.Contains("no options", StringComparison.OrdinalIgnoreCase));
        await Assert.That(warned).IsTrue();
    }

    [Test]
    public async Task CustomPoll_WithMissingOptionsKey_IsSkippedAndWarned()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        var spPollId = MemberUuid();

        // Same as the empty-array case but `options` is omitted entirely. SpPollContent maps
        // both shapes to a null `Options`, and the importer should treat them identically.
        var pollsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spPollId,
                content = new
                {
                    name = "synthetic choice no key",
                    desc = "",
                    custom = true,
                    endTime = 0L,
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, pollsJson: pollsJson);
        var (_, _, pollRepo, _, _, logger) = await RunImportAsync(stub, systemId);

        var polls = await pollRepo.ListAsync(systemId);
        await Assert.That(polls.Count).IsEqualTo(0);

        var warned = logger.Records.Any(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains(spPollId, StringComparison.Ordinal) &&
            r.Message.Contains("no options", StringComparison.OrdinalIgnoreCase));
        await Assert.That(warned).IsTrue();
    }

    [Test]
    public async Task Poll_VoteWithUnmappedMemberId_IsSkippedAndWarned()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        var spPollId = MemberUuid();
        var unmappedVoter = MemberUuid();

        // Two votes: one from the imported member (mapped → alter), one from a member uuid we
        // never imported. The unmapped vote previously slipped through as a raw 24-char SP id
        // in the `votes[i].id` field; we now drop it and surface a per-poll warning.
        var pollsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spPollId,
                content = new
                {
                    name = "synthetic vote",
                    desc = "",
                    custom = false,
                    endTime = 0L,
                    allowAbstain = true,
                    allowVeto = false,
                    votes = new[]
                    {
                        new { id = alpha, vote = "yes", comment = "synthetic-mapped" },
                        new { id = unmappedVoter, vote = "no", comment = "synthetic-unmapped" },
                    },
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, pollsJson: pollsJson);
        var (_, _, pollRepo, _, _, logger) = await RunImportAsync(stub, systemId);

        var polls = await pollRepo.ListAsync(systemId);
        await Assert.That(polls.Count).IsEqualTo(1);

        var responses = polls[0].Data.GetProperty("responses");
        await Assert.That(responses.GetArrayLength()).IsEqualTo(1);

        var onlyVote = responses[0];
        // The mapped vote carries the numeric alter id, never the SP uuid.
        await Assert.That(onlyVote.GetProperty("alter_id").ValueKind).IsEqualTo(JsonValueKind.Number);
        await Assert.That(onlyVote.GetProperty("vote").GetString()).IsEqualTo("yes");

        var warned = logger.Records.Any(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains(spPollId, StringComparison.Ordinal) &&
            r.Message.Contains("unmappable votes", StringComparison.OrdinalIgnoreCase));
        await Assert.That(warned).IsTrue();
    }

    [Test]
    public async Task Poll_VoteWithEmptyOrWhitespaceId_IsSkipped()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        var spPollId = MemberUuid();

        // Defensive coverage: SP's voteType only declares `id: string` without a minLength,
        // so blank ids could theoretically appear. They should never become rows in our data.
        var pollsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spPollId,
                content = new
                {
                    name = "synthetic blank-id votes",
                    desc = "",
                    custom = false,
                    endTime = 0L,
                    votes = new[]
                    {
                        new { id = "", vote = "yes", comment = "" },
                        new { id = "   ", vote = "no", comment = "" },
                        new { id = alpha, vote = "abstain", comment = "synthetic" },
                    },
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, pollsJson: pollsJson);
        var (_, _, pollRepo, _, _, _) = await RunImportAsync(stub, systemId);

        var polls = await pollRepo.ListAsync(systemId);
        await Assert.That(polls.Count).IsEqualTo(1);

        var responses = polls[0].Data.GetProperty("responses");
        await Assert.That(responses.GetArrayLength()).IsEqualTo(1);
        await Assert.That(responses[0].GetProperty("vote").GetString()).IsEqualTo("abstain");
    }

    [Test]
    public async Task Poll_VoteWithNullOrEmptyVoteString_IsSkipped()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        var spPollId = MemberUuid();

        // An empty `vote` string is meaningless — SP wouldn't render it anywhere — so we
        // refuse to import it even when the voter id is otherwise valid.
        var pollsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spPollId,
                content = new
                {
                    name = "synthetic blank-vote",
                    desc = "",
                    custom = false,
                    endTime = 0L,
                    votes = new[]
                    {
                        new { id = alpha, vote = "", comment = "synthetic-blank" },
                    },
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, pollsJson: pollsJson);
        var (_, _, pollRepo, _, _, _) = await RunImportAsync(stub, systemId);

        var polls = await pollRepo.ListAsync(systemId);
        await Assert.That(polls.Count).IsEqualTo(1);

        var data = polls[0].Data;
        await Assert.That(data.GetProperty("responses").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task NormalPoll_RoundTrips()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        var spPollId = MemberUuid();

        var pollsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spPollId,
                content = new
                {
                    name = "synthetic abstain vote",
                    desc = "synthetic desc",
                    custom = false,
                    endTime = Date_2021_06_10_End,
                    allowAbstain = true,
                    allowVeto = true,
                    votes = new[]
                    {
                        new { id = alpha, vote = "abstain", comment = "synthetic" },
                    },
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, pollsJson: pollsJson);
        var (_, _, pollRepo, _, _, _) = await RunImportAsync(stub, systemId);

        var polls = await pollRepo.ListAsync(systemId);
        await Assert.That(polls.Count).IsEqualTo(1);

        var poll = polls[0];
        await Assert.That(poll.Title).IsEqualTo("synthetic abstain vote");
        await Assert.That(poll.Description).IsEqualTo("synthetic desc");
        await Assert.That(poll.Type).IsEqualTo(PollType.Vote);
        await Assert.That(poll.TimeEnd).IsNotNull();
        await Assert.That(poll.TimeEnd!.Value)
            .IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(Date_2021_06_10_End).UtcDateTime);

        var responses = poll.Data.GetProperty("responses");
        await Assert.That(responses.GetArrayLength()).IsEqualTo(1);
        await Assert.That(responses[0].GetProperty("vote").GetString()).IsEqualTo("abstain");
        await Assert.That(responses[0].GetProperty("comment").GetString()).IsEqualTo("synthetic");
        // alter_id is the numeric alter id, never the raw SP member uuid.
        await Assert.That(responses[0].GetProperty("alter_id").ValueKind).IsEqualTo(JsonValueKind.Number);
        // SP's allowVeto flag carries over into the client's allow_veto member.
        await Assert.That(poll.Data.GetProperty("allow_veto").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task CustomPoll_RoundTrips()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        var spPollId = MemberUuid();

        var pollsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spPollId,
                content = new
                {
                    name = "synthetic choice",
                    desc = "",
                    custom = true,
                    endTime = 0L,
                    options = new object[]
                    {
                        new { name = "One", color = "#111111" },
                        new { name = "Two", color = "#222222" },
                        new { name = "Three", color = "#333333" },
                    },
                    votes = new[]
                    {
                        new { id = alpha, vote = "Two", comment = "synthetic" },
                    },
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, pollsJson: pollsJson);
        var (_, _, pollRepo, _, _, _) = await RunImportAsync(stub, systemId);

        var polls = await pollRepo.ListAsync(systemId);
        await Assert.That(polls.Count).IsEqualTo(1);

        var poll = polls[0];
        await Assert.That(poll.Title).IsEqualTo("synthetic choice");
        await Assert.That(poll.Type).IsEqualTo(PollType.Choice);

        var choices = poll.Data.GetProperty("choices");
        await Assert.That(choices.GetArrayLength()).IsEqualTo(3);
        await Assert.That(choices[0].GetProperty("name").GetString()).IsEqualTo("One");
        await Assert.That(choices[1].GetProperty("name").GetString()).IsEqualTo("Two");
        await Assert.That(choices[2].GetProperty("name").GetString()).IsEqualTo("Three");

        // The vote's option name translates to the minted choice id of the "Two" choice.
        var responses = poll.Data.GetProperty("responses");
        await Assert.That(responses.GetArrayLength()).IsEqualTo(1);
        await Assert.That(responses[0].GetProperty("choice_id").GetString())
            .IsEqualTo(choices[1].GetProperty("id").GetString());
        await Assert.That(responses[0].GetProperty("alter_id").ValueKind).IsEqualTo(JsonValueKind.Number);
    }

    [Test]
    public async Task Poll_LastOperationTime_PreservedAsInsertedAt()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        // 32-hex GUID, deliberately NOT a 24-hex ObjectId, so SpObjectId.TryDecodeTimestamp
        // fails and the importer falls back to lastOperationTime. This test now covers the
        // *fallback* branch of the ObjectId-primary cascade.
        var spPollId = MemberUuid();

        var pollsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spPollId,
                content = new
                {
                    name = "synthetic historical poll",
                    desc = "",
                    custom = false,
                    endTime = 0L,
                    lastOperationTime = Date_2020_01_15,
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, pollsJson: pollsJson);
        var (_, _, pollRepo, _, _, logger) = await RunImportAsync(stub, systemId);

        var polls = await pollRepo.ListAsync(systemId);
        await Assert.That(polls.Count).IsEqualTo(1);

        var expected = DateTimeOffset.FromUnixTimeMilliseconds(Date_2020_01_15).UtcDateTime;
        await Assert.That(polls[0].InsertedAt).IsEqualTo(expected);

        // Defensive: any accidental UtcNow substitution would land within seconds of "now".
        await Assert.That(polls[0].InsertedAt).IsLessThan(DateTime.UtcNow.AddDays(-30));

        // The fallback path must surface a per-row warning so operators can spot polls
        // whose creation date came from lastOperationTime (drifts on edits) rather than
        // from a real ObjectId.
        var warned = logger.Records.Any(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains(spPollId, StringComparison.Ordinal) &&
            r.Message.Contains("non-decodable 24-hex id", StringComparison.OrdinalIgnoreCase) &&
            r.Message.Contains("falling back to lastOperationTime", StringComparison.OrdinalIgnoreCase));
        await Assert.That(warned).IsTrue();
    }

    [Test]
    public async Task Poll_ImportedWithObjectIdTimestamp_PreservesDecodedUtc()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();

        // Golden 24-hex ObjectId: 0x671425cd = 1,729,373,645 = 2024-10-19 21:34:05Z. We set
        // a deliberately *different* lastOperationTime (Date_2020_01_15) so this test fails
        // loudly if a future regression makes the importer prefer lastOperationTime over
        // the decoded ObjectId.
        const string spPollIdHistorical = "671425cd02c63d13c59ba474";
        var expectedInsertedAt = new DateTime(2024, 10, 19, 21, 34, 5, DateTimeKind.Utc);

        var pollsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spPollIdHistorical,
                content = new
                {
                    name = "synthetic objectid poll",
                    desc = "",
                    custom = false,
                    endTime = 0L,
                    lastOperationTime = Date_2020_01_15,
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, pollsJson: pollsJson);
        var (_, _, pollRepo, _, _, logger) = await RunImportAsync(stub, systemId);

        var polls = await pollRepo.ListAsync(systemId);
        await Assert.That(polls.Count).IsEqualTo(1);

        using (Assert.Multiple())
        {
            await Assert.That(polls[0].InsertedAt).IsEqualTo(expectedInsertedAt);
            // ObjectId wins over lastOperationTime, so we must NOT land on the 2020 date.
            await Assert.That(polls[0].InsertedAt)
                .IsNotEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(Date_2020_01_15).UtcDateTime);
            // Defensive: an accidental UtcNow stamp would land within seconds of "now".
            await Assert.That(polls[0].InsertedAt).IsLessThan(DateTime.UtcNow.AddDays(-30));
        }

        // Happy path: no per-row warning should be emitted when the ObjectId decoded cleanly.
        var fallbackWarned = logger.Records.Any(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains(spPollIdHistorical, StringComparison.Ordinal) &&
            (r.Message.Contains("falling back", StringComparison.OrdinalIgnoreCase) ||
             r.Message.Contains("import time", StringComparison.OrdinalIgnoreCase)));
        await Assert.That(fallbackWarned).IsFalse();
    }

    [Test]
    public async Task Poll_LastOperationTimeMissing_FallsBackToImportTimeAndWarns()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();
        var spPollId = MemberUuid();

        // SP either omits `lastOperationTime` or sends 0 on very old rows. We accept those
        // but log a warning so we can spot polls that lost their creation timestamp.
        var pollsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spPollId,
                content = new
                {
                    name = "synthetic missing-lastop poll",
                    desc = "",
                    custom = false,
                    endTime = 0L,
                    lastOperationTime = 0L,
                },
            },
        });

        var importStarted = DateTime.UtcNow;
        var stub = BuildSp(sysId.Value, alpha, pollsJson: pollsJson);
        var (_, _, pollRepo, _, _, logger) = await RunImportAsync(stub, systemId);

        var polls = await pollRepo.ListAsync(systemId);
        await Assert.That(polls.Count).IsEqualTo(1);
        await Assert.That(polls[0].InsertedAt).IsGreaterThanOrEqualTo(importStarted.AddSeconds(-5));
        await Assert.That(polls[0].InsertedAt).IsLessThanOrEqualTo(DateTime.UtcNow.AddSeconds(5));

        var warned = logger.Records.Any(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains(spPollId, StringComparison.Ordinal) &&
            r.Message.Contains("no lastOperationTime", StringComparison.OrdinalIgnoreCase));
        await Assert.That(warned).IsTrue();
    }

    [Test]
    public async Task CustomField_ImportedWithObjectIdTimestamp_PreservesDecodedUtc()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();

        // The first 4 bytes of a MongoDB ObjectId are a big-endian Unix-seconds timestamp.
        // 0x671425cd = 1,729,373,645 = 2024-10-19 21:34:05Z. The remaining 16 hex chars are
        // synthetic - SP only uses them as machine/process/counter bytes and we ignore them.
        const string spFieldIdHistorical = "671425cd02c63d13c59ba474";
        var expectedInsertedAt = new DateTime(2024, 10, 19, 21, 34, 5, DateTimeKind.Utc);

        var customFieldsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spFieldIdHistorical,
                content = new
                {
                    name = "synthetic-favourite-colour",
                    type = 0,
                    supportMarkdown = false,
                    @private = false,
                    preventTrusted = false,
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, customFieldsJson: customFieldsJson);
        var (_, _, _, fieldRepo, _, _) = await RunImportAsync(stub, systemId);

        var fields = await fieldRepo.ListAsync(systemId);
        await Assert.That(fields.Count).IsEqualTo(1);

        var field = fields[0];
        using (Assert.Multiple())
        {
            await Assert.That(field.Name).IsEqualTo("synthetic-favourite-colour");
            await Assert.That(field.InsertedAt).IsEqualTo(expectedInsertedAt);
            await Assert.That(field.InsertedAt).IsNotNull();
            // Defensive: an accidental UtcNow stamp would land within seconds of "now".
            await Assert.That(field.InsertedAt!.Value).IsLessThan(DateTime.UtcNow.AddDays(-30));
        }
    }

    [Test]
    public async Task CustomField_WithNullSupportMarkdown_IsStillImported()
    {
        // Real-world regression: SP's update300 migration (SimplyPluralApi/src/api/v1/user/updates/update300.ts:187)
        // copies legacy field schemas via `insertOne({ ..., supportMarkdown: field.supportMarkdown })`
        // where the legacy `field.supportMarkdown` is undefined for pre-markdown rows. Mongo stores
        // undefined as null, the SP API serialises `"supportMarkdown":null`, and the importer's
        // SpCustomFieldContent.SupportMarkdown used to be a non-nullable bool. Result: the whole
        // customFields response failed to deserialise, FetchAsync's catch-all swallowed the
        // exception, and the import silently skipped the entire fields step (with alters then
        // landing with no Field values because the SP->our-id mapping was empty).
        //
        // Hand-written JSON here because anonymous types can't emit a JSON null for a bool member.
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();

        const string spFieldIdHistorical = "671425cd02c63d13c59ba474";
        var expectedInsertedAt = new DateTime(2024, 10, 19, 21, 34, 5, DateTimeKind.Utc);

        var customFieldsJson = $$"""
            [
              {
                "exists": true,
                "id": "{{spFieldIdHistorical}}",
                "content": {
                  "name": "synthetic-legacy-migrated-field",
                  "type": 0,
                  "supportMarkdown": null,
                  "buckets": []
                }
              }
            ]
            """;

        var stub = BuildSp(sysId.Value, alpha, customFieldsJson: customFieldsJson);
        var (_, _, _, fieldRepo, _, _) = await RunImportAsync(stub, systemId);

        var fields = await fieldRepo.ListAsync(systemId);
        await Assert.That(fields.Count)
            .IsEqualTo(1)
            .Because("legacy SP fields with supportMarkdown:null must still be imported - they're the common case on systems migrated via SP's update300 path.");

        // SP's own code defaults missing supportMarkdown to true (SimplyPluralApi/src/api/v2/user.ts:95,
        // generateReport.ts:117), so a null on the wire should produce the markdown variant ("text"),
        // not the plaintext variant.
        await Assert.That(fields[0].Type).IsEqualTo(FieldType.Text);
        await Assert.That(fields[0].InsertedAt).IsEqualTo(expectedInsertedAt);
    }

    [Test]
    public async Task CustomField_WithUnparseableSpId_IsSkippedAndWarned()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();

        // SP always produces valid 24-hex ObjectIds in practice, but if the API ever returns
        // anything else we must skip rather than silently stamp UtcNow (which is what the
        // pre-fix code did for every field). One per-row warning lets operators trace the skip.
        const string spFieldIdGarbage = "not-an-objectid";

        var customFieldsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spFieldIdGarbage,
                content = new
                {
                    name = "synthetic-cannot-decode",
                    type = 0,
                    supportMarkdown = false,
                    @private = false,
                    preventTrusted = false,
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, customFieldsJson: customFieldsJson);
        var (_, _, _, fieldRepo, _, logger) = await RunImportAsync(stub, systemId);

        var fields = await fieldRepo.ListAsync(systemId);
        await Assert.That(fields.Count).IsEqualTo(0);

        var warned = logger.Records.Any(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains(spFieldIdGarbage, StringComparison.Ordinal) &&
            r.Message.Contains("24-hex ObjectId", StringComparison.OrdinalIgnoreCase));
        await Assert.That(warned).IsTrue();
    }

    [Test]
    public async Task Tag_ImportedWithObjectIdTimestamp_PreservesDecodedUtc()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();

        // Same golden ObjectId we use in the custom-fields test. 0x671425cd = 1,729,373,645
        // = 2024-10-19 21:34:05Z. Tags (SP "groups") inherit the same ObjectId-decoding
        // strategy as fields because SP exposes no per-row creation timestamp for groups.
        const string spGroupIdHistorical = "671425cd02c63d13c59ba474";
        var expectedInsertedAt = new DateTime(2024, 10, 19, 21, 34, 5, DateTimeKind.Utc);

        var groupsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spGroupIdHistorical,
                content = new
                {
                    name = "synthetic-friends-group",
                    desc = "",
                    color = "",
                    parent = "root",
                    members = Array.Empty<string>(),
                    @private = false,
                    preventTrusted = false,
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, groupsJson: groupsJson);
        var (_, _, _, _, tagRepo, _) = await RunImportAsync(stub, systemId);

        var tags = await tagRepo.ListAsync(systemId);
        await Assert.That(tags.Count).IsEqualTo(1);

        var tag = tags[0];
        using (Assert.Multiple())
        {
            await Assert.That(tag.Name).IsEqualTo("synthetic-friends-group");
            await Assert.That(tag.InsertedAt).IsEqualTo(expectedInsertedAt);
            // Defensive: an accidental UtcNow stamp would land within seconds of "now".
            await Assert.That(tag.InsertedAt).IsLessThan(DateTime.UtcNow.AddDays(-30));
        }
    }

    [Test]
    public async Task Tag_WithUnparseableSpId_IsSkippedAndWarned()
    {
        var sysId = Uid();
        var systemId = Uid();
        var alpha = MemberUuid();

        // SP always produces valid 24-hex ObjectIds for groups in practice, but if the API
        // ever returns anything else we must skip rather than silently stamp UtcNow. One
        // per-row warning lets operators trace the skip.
        const string spGroupIdGarbage = "not-an-objectid";

        var groupsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spGroupIdGarbage,
                content = new
                {
                    name = "synthetic-cannot-decode-group",
                    desc = "",
                    color = "",
                    parent = "root",
                    members = Array.Empty<string>(),
                    @private = false,
                    preventTrusted = false,
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha, groupsJson: groupsJson);
        var (_, _, _, _, tagRepo, logger) = await RunImportAsync(stub, systemId);

        var tags = await tagRepo.ListAsync(systemId);
        await Assert.That(tags.Count).IsEqualTo(0);

        var warned = logger.Records.Any(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains(spGroupIdGarbage, StringComparison.Ordinal) &&
            r.Message.Contains("24-hex ObjectId", StringComparison.OrdinalIgnoreCase));
        await Assert.That(warned).IsTrue();
    }

    [Test]
    public async Task Alter_NoDate_FallsBackToObjectIdAndWarns()
    {
        var sysId = Uid();
        var systemId = Uid();

        // 24-hex MongoDB ObjectId: 0x671425cd = 1,729,373,645 = 2024-10-19 21:34:05Z.
        // The importer used to silently stamp 1970-01-01 whenever SP returned date == 0.
        // We now fall back to the ObjectId-decoded creation second and warn once per row.
        // We can't assert the resulting CreatedAt directly (AlterReadModel doesn't expose
        // it), but the warning fired is a sufficient witness that the new cascade took
        // the ObjectId branch.
        const string spMemberIdHistorical = "671425cd02c63d13c59ba474";

        var membersJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spMemberIdHistorical,
                content = new
                {
                    name = "synthetic-no-date-alter",
                    date = 0L,
                    lastOperationTime = Date_2022_03_05,
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha: spMemberIdHistorical, membersJson: membersJson);
        var (_, _, _, _, _, logger) = await RunImportAsync(stub, systemId);

        var warned = logger.Records.Any(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains(spMemberIdHistorical, StringComparison.Ordinal) &&
            r.Message.Contains("no date", StringComparison.OrdinalIgnoreCase) &&
            r.Message.Contains("ObjectId-decoded", StringComparison.OrdinalIgnoreCase));
        await Assert.That(warned).IsTrue();

        // The UtcNow-fallback warning must NOT also fire: ObjectId decode succeeded so we
        // shouldn't have walked further down the cascade.
        var utcNowWarned = logger.Records.Any(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains(spMemberIdHistorical, StringComparison.Ordinal) &&
            r.Message.Contains("import time as created date", StringComparison.OrdinalIgnoreCase));
        await Assert.That(utcNowWarned).IsFalse();
    }

    [Test]
    public async Task Alter_NoDateAndNonDecodableId_FallsBackToUtcNowAndWarns()
    {
        var sysId = Uid();
        var systemId = Uid();
        // 32-hex GUID, deliberately not a 24-hex ObjectId, so SpObjectId.TryDecodeTimestamp
        // fails and we walk to the terminal UtcNow + warn branch. Alters can't be skipped
        // (downstream associations depend on them), so the terminal branch must still
        // create the row.
        var spMemberIdGarbage = MemberUuid();

        var membersJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = spMemberIdGarbage,
                content = new
                {
                    name = "synthetic-garbage-id-alter",
                    date = 0L,
                    lastOperationTime = Date_2022_03_05,
                },
            },
        });

        var stub = BuildSp(sysId.Value, alpha: spMemberIdGarbage, membersJson: membersJson);
        var (_, _, _, _, _, logger) = await RunImportAsync(stub, systemId);

        var warned = logger.Records.Any(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains(spMemberIdGarbage, StringComparison.Ordinal) &&
            r.Message.Contains("no date and non-decodable id", StringComparison.OrdinalIgnoreCase) &&
            r.Message.Contains("import time as created date", StringComparison.OrdinalIgnoreCase));
        await Assert.That(warned).IsTrue();
    }

    /// <summary>
    /// Builds the canonical "minimal valid SP backend" stub. The single SP member <paramref name="alpha"/>
    /// is wired up so the import creates exactly one alter, which is the only association
    /// front-related assertions care about. Override the front-related blobs per test.
    /// </summary>
    private static TestServices.StubSpHandler BuildSp(
        string sysId,
        string alpha,
        string? frontHistoryJson = null,
        string? currentFrontersJson = null,
        string? pollsJson = null,
        string? customFieldsJson = null,
        string? groupsJson = null,
        string? membersJson = null)
    {
        var meJson = JsonSerializer.Serialize(new
        {
            exists = true,
            id = sysId,
            content = new { desc = "synthetic test system" },
        });

        var defaultMembersJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                exists = true,
                id = alpha,
                content = new
                {
                    name = "alpha",
                    date = Date_2022_03_05,
                    lastOperationTime = Date_2022_03_05,
                },
            },
        });

        return new TestServices.StubSpHandler()
            .OnGet("/v1/me", meJson)
            .OnGet($"/v1/customFields/{sysId}", customFieldsJson ?? "[]")
            .OnGet($"/v1/members/{sysId}", membersJson ?? defaultMembersJson)
            .OnGet($"/v1/customFronts/{sysId}", "[]")
            .OnGet($"/v1/groups/{sysId}", groupsJson ?? "[]")
            .OnGetPrefix($"/v1/frontHistory/{sysId}", frontHistoryJson ?? "[]")
            .OnGet("/v1/fronters/", currentFrontersJson ?? "[]")
            .OnGet($"/v1/polls/{sysId}", pollsJson ?? "[]");
    }

    private static async Task<(ImportJobOutcome Result, IFrontingRepository FrontingRepo, IPollRepository PollRepo, ISettingsFieldRepository FieldRepo, ITagRepository TagRepo, CapturingLogger Logger)> RunImportAsync(
        TestServices.StubSpHandler stub,
        SystemId systemId)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationManager());

        var logger = new CapturingLogger();
        services.AddSingleton<ILoggerFactory>(new CapturingLoggerFactory(logger));
        services.AddLogging();

        InMemoryServiceCollectionExtensions.Register();
        services.AddInterfoldPersistence(PersistenceMode.InMemory, cfg =>
        {
            cfg.ScyllaKeyspace = ScyllaKeyspace.Nam;
        });
        services.AddInterfoldDomainHandlers();

        services.AddOptions<AuthenticationConfiguration>();
        services.AddSingleton<IAvatarStorage, NullAvatarStorage>();
        // SimplyPluralImportService takes TimeProvider via ctor injection (R2-C15).
        services.AddSingleton(TimeProvider.System);

        services.AddHttpClient(HttpClientNames.SimplyPlural)
            .ConfigurePrimaryHttpMessageHandler(() => stub);

        // -Core overload skips the async IImportJobRunner queue consumer that the full
        // AddSimplyPluralImport extension registers (Program.cs uses that). Tests drive
        // ImportAsync directly so the runner would be dead weight.
        services.AddSimplyPluralImportCore();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var importService = scope.ServiceProvider.GetRequiredService<ISimplyPluralImportService>();
        importService.WaitForAvatars = true;

        // encryptionKey is null → ImportAsync skips ValidateEncryptionKeyAsync; notes are skipped too.
        // spToken is a synthetic placeholder — StubSpHandler doesn't check it.
        var result = await importService.ImportAsync(systemId, new ImportToken("synthetic-token"), encryptionKey: null);

        var frontingRepo = scope.ServiceProvider.GetRequiredService<IFrontingRepository>();
        var pollRepo = scope.ServiceProvider.GetRequiredService<IPollRepository>();
        var fieldRepo = scope.ServiceProvider.GetRequiredService<ISettingsFieldRepository>();
        var tagRepo = scope.ServiceProvider.GetRequiredService<ITagRepository>();
        return (result, frontingRepo, pollRepo, fieldRepo, tagRepo, logger);
    }

    private sealed class NullAvatarStorage : IAvatarStorage
    {
        public Task<AvatarUrl> SaveSystemAvatarAsync(SystemId systemId, Stream stream, CancellationToken cancellationToken = default)
            => Task.FromResult(new AvatarUrl(string.Empty));

        public Task<AvatarUrl> SaveAlterAvatarAsync(SystemId systemId, AlterId alterId, Stream stream, CancellationToken cancellationToken = default)
            => Task.FromResult(new AvatarUrl(string.Empty));

        public Task<bool> DeleteByUrlAsync(AvatarUrl? avatarUrl, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    /// <summary>
    /// Minimal in-tree FakeLogger replacement. Avoids pulling in
    /// <c>Microsoft.Extensions.Diagnostics.Testing</c> just for one assertion. Captures the
    /// formatted message and level so tests can grep for the specific warning emitted by
    /// the skip+warn branch in ImportFrontsAsync.
    /// </summary>
    private sealed record CapturedLog(LogLevel Level, string Message, string? Category);

    private sealed class CapturingLogger : ILogger
    {
        public List<CapturedLog> Records { get; } = new();
        public string? Category { get; set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Records)
            {
                Records.Add(new CapturedLog(logLevel, formatter(state, exception), Category));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly CapturingLogger _logger;
        public CapturingLoggerFactory(CapturingLogger logger) => _logger = logger;

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName)
        {
            _logger.Category ??= categoryName;
            return _logger;
        }

        public void Dispose() { }
    }
}
