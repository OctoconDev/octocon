using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Interfold.Api.Services.SimplyPlural;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Domain;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Microsoft.Extensions.Options;
using Interfold.Contracts;
using Interfold.Contracts.Models.ImportOperations;

namespace Interfold.Api.Services;

/*
  TODO: We currently don't import the following SP data
  * Privacy Buckets (we don't have this concept within Interfold, and they don't always 1:1 to our security levels)
  * Message Boards - Alter (we don't have this concept)
    * Receive board messages on switch
  * Prevent notification - On alter front
  * Chat - Can maybe map to global journals?
  * App Reminders
  * Friend Settings
    * Shared Members
    * See who is fronting
    * Global front change notifications (them and us)
    * Privacy Buckets
*/

public sealed class SimplyPluralImportService : ISimplyPluralImportService
{
    private const int MaxJournalContentLength = 30_000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAlterRepository _alterRepository;
    private readonly ITagRepository _tagRepository;
    private readonly IFrontingRepository _frontingRepository;
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IAccountRepository _accountRepository;
    private readonly IPollRepository _pollRepository;
    private readonly IJournalRepository _journalRepository;
    private readonly IAvatarStorage _avatarStorage;
    private readonly IEncryptionStateRepository _encryptionStateRepository;
    private readonly IOptionsMonitor<AuthenticationConfiguration> _authOptions;
    private readonly ILogger<SimplyPluralImportService> _logger;

    public SimplyPluralImportService(
        IHttpClientFactory httpClientFactory,
        IAlterRepository alterRepository,
        ITagRepository tagRepository,
        IFrontingRepository frontingRepository,
        ISettingsFieldRepository fieldRepository,
        IAccountRepository accountRepository,
        IPollRepository pollRepository,
        IJournalRepository journalRepository,
        IAvatarStorage avatarStorage,
        ILogger<SimplyPluralImportService> logger, IEncryptionStateRepository encryptionStateRepository, IOptionsMonitor<AuthenticationConfiguration> authConfig)
    {
        _httpClientFactory = httpClientFactory;
        _alterRepository = alterRepository;
        _tagRepository = tagRepository;
        _frontingRepository = frontingRepository;
        _fieldRepository = fieldRepository;
        _accountRepository = accountRepository;
        _pollRepository = pollRepository;
        _journalRepository = journalRepository;
        _avatarStorage = avatarStorage;
        _logger = logger;
        _encryptionStateRepository = encryptionStateRepository;
        _authOptions = authConfig;
    }

    public bool? WaitForAvatars { get; set; }
    
    public async Task<SpImportResult> ImportAsync(
        SystemId systemId,
        ImportToken spToken,
        RecoveryCode? recoveryKey,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Simply Plural import for system {SystemId}", systemId);

        // Recovery code and derived key stay typed end-to-end through the import: the
        // "no derived key" case is a null EncryptionKeyMaterial? rather than an empty
        // string, pattern-matched at the gate below.
        EncryptionKeyMaterial? encryptionKey = null;
        if (recoveryKey is { } providedRecoveryKey && !string.IsNullOrWhiteSpace(providedRecoveryKey.Value))
        {
            var (encryptionValidation, derivedKey) = await ValidateEncryptionKeyAsync(systemId, providedRecoveryKey, cancellationToken);
            if (!encryptionValidation.Success)
                return encryptionValidation;

            encryptionKey = derivedKey;
        }

        using var httpClient = _httpClientFactory.CreateClient(HttpClientNames.SimplyPlural);
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", spToken.Value); // SP uses non-standard "Authorization: {token}" header
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Interfold/spimport");

        // 1. Fetch system data
        var systemData = await FetchAsync<SpEntity<SpSystemContent>>(httpClient, SpApiPaths.Me(), cancellationToken);
        if (systemData is null)
            return new SpImportResult(false, 0, ImportErrorCode.SpImportFailed, "Failed to fetch system data from Simply Plural.");

        var spSystemId = systemData.Id;
        var description = systemData.Content.Desc;

        // 2. Fetch custom fields
        var (fieldMapping, createdFieldIds) = await ImportCustomFieldsAsync(httpClient, spSystemId, systemId, cancellationToken);

        // 3. Fetch members and custom fronts
        var (alterCount, alterAssociations, avatarDownloads) =
            await ImportAltersAsync(httpClient, spSystemId, systemId, fieldMapping, cancellationToken);

        // 4. Fetch groups -> tags
        var tagAssociations = await ImportTagsAsync(httpClient, spSystemId, systemId, alterAssociations, cancellationToken);

        // 5. Fetch front history
        await ImportFrontsAsync(httpClient, spSystemId, systemId, alterAssociations, cancellationToken);

        // 6. Import polls
        await ImportPollsAsync(httpClient, spSystemId, systemId, alterAssociations, cancellationToken);

        // (optional) 7. Import notes per alter as alter journals
        if (encryptionKey is { } derivedEncryptionKey)
        {
            await ImportNotesAsync(httpClient, spSystemId, systemId, alterAssociations, derivedEncryptionKey, cancellationToken);
        }

        // 8. Update account description if available
        if (!string.IsNullOrWhiteSpace(description))
        {
            var truncated = description.Length > 3000 ? description[..3000] : description;
            await _accountRepository.UpdateDescriptionAsync(systemId, truncated, cancellationToken);
        }

        // 8b. Decide what to do with the system avatar. SP CDN avatars are queued for
        // rehost (since SP is shutting down); a non-CDN URL is written through synchronously
        // with avatar_source=External so the avatar is visible immediately.
        var systemAvatarRehost = await ImportSystemAvatarAsync(systemId, systemData.Content, cancellationToken);
        if (systemAvatarRehost is not null)
        {
            avatarDownloads.Add(systemAvatarRehost);
        }

        // 9. Download and attach avatars in background
        if (WaitForAvatars == true)
        {
            await DownloadAvatarsAsync(systemId, avatarDownloads, cancellationToken);
        }
        else
        {
            _ = Task.Run(async () => await DownloadAvatarsAsync(systemId, avatarDownloads, CancellationToken.None),
                CancellationToken.None);
        }

        _logger.LogInformation("Simply Plural import complete for system {SystemId}: {AlterCount} alters imported", systemId, alterCount);
        return new SpImportResult(true, alterCount);
    }

    private async Task<(Dictionary<string, FieldId> FieldMapping, List<FieldId> CreatedFieldIds)> ImportCustomFieldsAsync(
        HttpClient httpClient, string spSystemId, SystemId systemId, CancellationToken ct)
    {
        var fieldMapping = new Dictionary<string, FieldId>(); // SP field ID -> our field ID
        var createdFieldIds = new List<FieldId>();

        var customFields = await FetchAsync<List<SpEntity<SpCustomFieldContent>>>(httpClient, SpApiPaths.CustomFields(spSystemId), ct);
        if (customFields is null)
            return (fieldMapping, createdFieldIds);

        foreach (var field in customFields)
        {
            var spFieldId = field.Id;

            // SP doesn't return a created/lastOperationTime on custom fields - the only signal
            // is the MongoDB ObjectId's first 4 bytes (big-endian Unix seconds). If the id
            // isn't a 24-hex ObjectId we have nothing reliable to stamp, so skip rather than
            // silently falling back to UtcNow (which is what we used to do for everything).
            if (!SpObjectId.TryDecodeTimestamp(spFieldId, out var insertedAtUtc))
            {
                _logger.LogWarning(
                    "Skipping SP custom field {SpFieldId} - id is not a 24-hex ObjectId so we cannot derive its created date",
                    spFieldId);
                continue;
            }

            var name = field.Content.Name ?? "Unnamed field";
            if (name.Length > 100) name = name[..100];

            var fieldType = MapFieldType(field.Content.Type, field.Content.SupportMarkdown);
            var securityLevel = MapSecurityLevel(field.Content.Private, field.Content.PreventTrusted);
            var createdId = await _fieldRepository.CreateAsync(systemId, name, fieldType, securityLevel, false, insertedAtUtc, ct);
            if (createdId is not null)
            {
                fieldMapping[spFieldId] = createdId.Value;
                createdFieldIds.Add(createdId.Value);
            }
        }

        return (fieldMapping, createdFieldIds);
    }

    private async Task<(int AlterCount, Dictionary<string, AlterId> AlterAssociations, List<AvatarDownload> AvatarDownloads)> ImportAltersAsync(
        HttpClient httpClient, string spSystemId, SystemId systemId,
        Dictionary<string, FieldId> fieldMapping, CancellationToken ct)
    {
        var alterAssociations = new Dictionary<string, AlterId>(); // SP UUID -> our alter ID
        var avatarDownloads = new List<AvatarDownload>();
        var alterCount = 0;

        // Fetch members
        var members = await FetchAsync<List<SpEntity<SpMemberContent>>>(httpClient, SpApiPaths.Members(spSystemId), ct);
        // Fetch custom fronts
        var customFronts = await FetchAsync<List<SpEntity<SpMemberContent>>>(httpClient, SpApiPaths.CustomFronts(spSystemId), ct);

        var allEntries = new List<(SpMemberContent Content, string Uuid, bool IsCustomFront)>();

        if (members is not null)
        {
            foreach (var member in members)
                allEntries.Add((member.Content, member.Id, false));
        }

        if (customFronts is not null)
        {
            foreach (var cf in customFronts)
                allEntries.Add((cf.Content, cf.Id, true));
        }

        foreach (var (content, uuid, isCustomFront) in allEntries)
        {
            var name = content.Name ?? "Unnamed alter";
            if (name.Length > 80) name = name[..80];
            if (string.IsNullOrWhiteSpace(name)) name = "Unnamed alter";

            // SP's `date` field is the real created-date when present; if it's 0 (older
            // rows, occasional schema gaps) we previously stamped 1970-01-01 silently.
            // Fall back to the SP member id's ObjectId-encoded creation second, then to
            // import time (alter creation can't be skipped: downstream fronts/polls/notes
            // look the alter up by SP uuid via alterAssociations).
            DateTimeOffset createdAt;
            if (content.Date > 0)
            {
                createdAt = UnixTimestampToDateTimeOffset(content.Date);
            }
            else if (SpObjectId.TryDecodeTimestamp(uuid, out var decodedAlterAt))
            {
                createdAt = new DateTimeOffset(decodedAlterAt, TimeSpan.Zero);
                _logger.LogWarning(
                    "SP alter {SpMemberId} for system {SystemId} has no date; falling back to ObjectId-decoded creation timestamp.",
                    uuid, systemId);
            }
            else
            {
                createdAt = DateTimeOffset.UtcNow;
                _logger.LogWarning(
                    "SP alter {SpMemberId} for system {SystemId} has no date and non-decodable id; using import time as created date.",
                    uuid, systemId);
            }

            var updatedAt = UnixTimestampToDateTimeOffset(content.LastOperationTime);

            var alterId = await _alterRepository.CreateAsync(systemId, new CreateAlterCommand(name, createdAt), ct);
            if (alterId is null)
                continue;

            alterAssociations[uuid] = alterId.Value;
            alterCount++;

            // Build update command with SP data
            var pronouns = content.Pronouns;
            if (pronouns?.Length > 50) pronouns = pronouns[..50];

            var desc = content.Desc;
            if (desc?.Length > 3000) desc = desc[..3000];

            var color = ParseColor(content.Color);

            // Parse fields
            List<AlterFieldCommand>? fields = null;
            if (content.Info is { Count: > 0 })
            {
                fields = new List<AlterFieldCommand>();
                foreach (var (spFieldId, value) in content.Info)
                {
                    if (fieldMapping.TryGetValue(spFieldId, out var ourFieldId) && !string.IsNullOrWhiteSpace(value))
                        fields.Add(new AlterFieldCommand(ourFieldId, value));
                }

                if (fields.Count == 0) fields = null;
            }

            var securityLevel = MapSecurityLevel(content.Private, content.PreventTrusted);

            // Resolve the SP avatar URL up-front so we can decide whether the immediate
            // UpdateAlterCommand carries a passthrough URL or stays empty (rehost case).
            //
            // Branches:
            //   * avatarUuid + uid  → constructed SP CDN URL → rehost (SP is shutting down).
            //   * avatarUrl on SP CDN → rehost as above.
            //   * avatarUrl elsewhere → passthrough with avatar_source=External.
            //   * none of the above → no avatar set.
            var avatarUuid = content.AvatarUuid;
            var rawAvatarUrl = content.AvatarUrl;
            var uid = content.Uid;
            string? rehostUrl = null;
            string? passthroughUrl = null;

            if (!string.IsNullOrWhiteSpace(avatarUuid) && !string.IsNullOrWhiteSpace(uid))
            {
                rehostUrl = SpCdnHosts.Avatar(uid, avatarUuid);
            }
            else if (!string.IsNullOrWhiteSpace(rawAvatarUrl) && Uri.IsWellFormedUriString(rawAvatarUrl, UriKind.Absolute))
            {
                if (IsSpCdnUrl(rawAvatarUrl))
                {
                    rehostUrl = rawAvatarUrl;
                }
                else
                {
                    passthroughUrl = rawAvatarUrl;
                }
            }

            var updateCommand = new UpdateAlterCommand(
                AlterId: alterId.Value,
                Name: null, // already set via CreateAsync
                Description: desc,
                AvatarUrl: AvatarUrl.FromNullable(passthroughUrl),
                AvatarSource: passthroughUrl is null ? null : AvatarSource.External,
                Color: HexColor.FromNullable(color),
                Pronouns: pronouns,
                SecurityLevel: securityLevel,
                Fields: fields,
                ProxyName: null,
                Alias: null,
                Untracked: isCustomFront,
                Archived: content.Archived,
                Pinned: false,
                UpdatedAt: updatedAt
            );

            await _alterRepository.UpdateAsync(systemId, updateCommand, ct);

            if (rehostUrl is not null)
            {
                avatarDownloads.Add(new AvatarDownload(rehostUrl, systemId, AvatarKind.Alter, alterId.Value));
            }
        }

        return (alterCount, alterAssociations, avatarDownloads);
    }

    private async Task<Dictionary<string, TagId>> ImportTagsAsync(
        HttpClient httpClient, string spSystemId, SystemId systemId,
        Dictionary<string, AlterId> alterAssociations, CancellationToken ct)
    {
        var tagAssociations = new Dictionary<string, TagId>(); // SP group ID -> our tag ID

        var groups = await FetchAsync<List<SpEntity<SpGroupContent>>>(httpClient, SpApiPaths.Groups(spSystemId), ct);
        if (groups is null)
            return tagAssociations;

        // First pass: create all tags (without parent relationships)
        var tagEntries = new List<(string SpId, SpGroupContent Content)>();
        foreach (var group in groups)
        {
            tagEntries.Add((group.Id, group.Content));
        }

        // Create tags
        foreach (var (spId, content) in tagEntries)
        {
            var name = content.Name ?? "Unnamed tag";
            if (name.Length > 100) name = name[..100];
            if (string.IsNullOrWhiteSpace(name)) name = "Unnamed tag";

            if (!SpObjectId.TryDecodeTimestamp(spId, out var insertedAtUtc))
            {
                _logger.LogWarning(
                    "Skipping SP group (tag) {SpGroupId} - id is not a 24-hex ObjectId so we cannot derive its created date",
                    spId);
                continue;
            }

            var createdTagId = await _tagRepository.CreateAsync(systemId, new CreateTagCommand(name, null, insertedAtUtc), ct);
            if (createdTagId is not null)
            {
                tagAssociations[spId] = createdTagId.Value;

                // Update tag with description and color from SP
                var tagDesc = content.Desc;
                if (tagDesc?.Length > 1000) tagDesc = tagDesc[..1000];
                var tagColor = ParseColor(content.Color);

                var tagSecurityLevel = MapSecurityLevel(content.Private, content.PreventTrusted);

                if (!string.IsNullOrWhiteSpace(tagDesc) || !string.IsNullOrWhiteSpace(tagColor) || tagSecurityLevel != VisibilityLevel.Private)
                {
                    await _tagRepository.UpdateAsync(systemId, new UpdateTagCommand(
                        TagId: createdTagId.Value,
                        Name: null,
                        Color: HexColor.FromNullable(tagColor),
                        Description: tagDesc,
                        SecurityLevel: tagSecurityLevel
                    ), ct);
                }
            }
        }

        // Second pass: set parent relationships
        foreach (var (spId, content) in tagEntries)
        {
            if (!tagAssociations.TryGetValue(spId, out var ourTagId))
                continue;

            var parent = content.Parent;
            if (string.IsNullOrWhiteSpace(parent) || parent == SpApiPaths.RootGroupParent)
                continue;

            if (tagAssociations.TryGetValue(parent, out var parentTagId))
            {
                await _tagRepository.SetParentAsync(systemId, ourTagId, parentTagId, ct);
            }
        }

        // Third pass: attach alters to tags
        foreach (var (spId, content) in tagEntries)
        {
            if (!tagAssociations.TryGetValue(spId, out var ourTagId))
                continue;

            if (content.Members is null)
                continue;

            foreach (var memberUuid in content.Members)
            {
                if (alterAssociations.TryGetValue(memberUuid, out var alterId))
                {
                    await _tagRepository.AttachAlterAsync(systemId, ourTagId, alterId, ct);
                }
            }
        }

        return tagAssociations;
    }

    private async Task ImportFrontsAsync(
        HttpClient httpClient, string spSystemId, SystemId systemId,
        Dictionary<string, AlterId> alterAssociations, CancellationToken ct)
    {
        // Fetch front history in chunks (SP epoch: Jan 1, 2015)
        const long startEpoch = 1_420_070_400_000;
        const int monthInterval = 6;
        var chunkSizeMs = (long)monthInterval * 30 * 24 * 60 * 60 * 1000;
        var endTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var numberOfChunks = (int)Math.Ceiling((double)(endTimeMs - startEpoch) / chunkSizeMs);

        var seenFrontIds = new HashSet<string>();

        for (var i = 0; i < numberOfChunks; i++)
        {
            var chunkStart = startEpoch + i * chunkSizeMs;
            var chunkEnd = Math.Min(startEpoch + (i + 1) * chunkSizeMs, endTimeMs);

            var fronts = await FetchAsync<List<SpEntity<SpFrontContent>>>(httpClient,
                SpApiPaths.FrontHistory(spSystemId, chunkStart, chunkEnd), ct);

            if (fronts is not null)
            {
                foreach (var front in fronts)
                {
                    if (!seenFrontIds.Add(front.Id))
                        continue;

                    var memberId = front.Content.Member;
                    if (memberId is null || !alterAssociations.TryGetValue(memberId, out var alterId))
                        continue;

                    if (front.Content.StartTime <= 0 || front.Content.EndTime <= 0)
                        continue;

                    //TODO: Add comments on top of the custom status
                    var comment = front.Content.CustomStatus;
                    if (comment?.Length > 50) comment = comment[..50];

                    var spStart = DateTimeOffset.FromUnixTimeMilliseconds(front.Content.StartTime);
                    var spEnd = DateTimeOffset.FromUnixTimeMilliseconds(front.Content.EndTime);

                    await _frontingRepository.StartAsync(systemId, alterId, comment, spStart, ct);
                    await _frontingRepository.EndAsync(systemId, alterId, spEnd, ct);
                }
            }

            // Rate limit courtesy delay
            await Task.Delay(200, ct);
        }

        // Import current fronters
        var currentFronters = await FetchAsync<List<SpEntity<SpFrontContent>>>(httpClient, SpApiPaths.CurrentFronters(), ct);
        if (currentFronters is not null)
        {
            foreach (var fronter in currentFronters)
            {
                var memberId = fronter.Content.Member;
                if (memberId is null || !alterAssociations.TryGetValue(memberId, out var alterId))
                    continue;

                //TODO: Add comments on top of the custom status
                var comment = fronter.Content.CustomStatus;
                if (comment?.Length > 50) comment = comment[..50];

                // Prefer the front's own startTime; if SP didn't emit one (sticky/primary
                // live fronters can omit it), fall back to the SP MongoDB ObjectId's
                // encoded creation second. ObjectId is the doc's birth moment which for a
                // live front is when the user switched — better than lastOperationTime,
                // which drifts on every edit. Skip+warn if neither is available rather
                // than silently stamping UtcNow (the original "today date" bug).
                DateTimeOffset spStart;
                if (fronter.Content.StartTime > 0)
                {
                    spStart = DateTimeOffset.FromUnixTimeMilliseconds(fronter.Content.StartTime);
                }
                else if (SpObjectId.TryDecodeTimestamp(fronter.Id, out var decodedFrontAt))
                {
                    spStart = new DateTimeOffset(decodedFrontAt, TimeSpan.Zero);
                }
                else
                {
                    _logger.LogWarning(
                        "Skipping live SP fronter for system {SystemId} member {MemberId}: no startTime and SP front id is not a 24-hex ObjectId.",
                        systemId, memberId);
                    continue;
                }

                await _frontingRepository.StartAsync(systemId, alterId, comment, spStart, ct);
            }
        }
    }

    private async Task ImportPollsAsync(
        HttpClient httpClient, string spSystemId, SystemId systemId,
        Dictionary<string, AlterId> alterAssociations, CancellationToken ct)
    {
        var polls = await FetchAsync<List<SpEntity<SpPollContent>>>(httpClient, SpApiPaths.Polls(spSystemId), ct);
        if (polls is null)
            return;

        foreach (var poll in polls)
        {
            // Custom polls without options would land as zero-choice "choice" rows that users
            // can't vote on. SP marks `options` as optional in its v1 poll schema, so this is
            // a real shape that turns up in the wild.
            if (poll.Content.Custom && (poll.Content.Options is null || poll.Content.Options.Count == 0))
            {
                _logger.LogWarning(
                    "Skipping custom SP poll {PollId} for system {SystemId}: custom poll has no options.",
                    poll.Id, systemId);
                continue;
            }

            // Match the CreatePollCommandHandler invariants (title <= 100, desc <= 2000) so
            // every imported row could also be edited through the public API later.
            var title = poll.Content.Name ?? "Unnamed poll";
            if (title.Length > 100) title = title[..100];
            if (string.IsNullOrWhiteSpace(title)) title = "Unnamed poll";

            var desc = poll.Content.Desc;
            if (desc?.Length > 2000) desc = desc[..2000];

            // SP custom=false → "vote" (yes/no/abstain/veto), custom=true → "choice" (multiple options)
            var type = poll.Content.Custom ? PollType.Choice : PollType.Vote;

            DateTime? timeEnd = poll.Content.EndTime > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(poll.Content.EndTime).UtcDateTime
                : null;

            // SP's poll id is a MongoDB ObjectId whose first 4 bytes encode the doc's
            // creation second. That's a true created-date; lastOperationTime drifts forward
            // on every edit and isn't what we want as inserted_at. Fall back to
            // lastOperationTime (then UtcNow) only when the id isn't a 24-hex ObjectId.
            DateTime insertedAt;
            if (SpObjectId.TryDecodeTimestamp(poll.Id, out var decodedPollAt))
            {
                insertedAt = decodedPollAt;
            }
            else if (poll.Content.LastOperationTime > 0)
            {
                insertedAt = DateTimeOffset.FromUnixTimeMilliseconds(poll.Content.LastOperationTime).UtcDateTime;
                _logger.LogWarning(
                    "SP poll {PollId} for system {SystemId} has non-decodable 24-hex id; falling back to lastOperationTime for inserted_at.",
                    poll.Id, systemId);
            }
            else
            {
                insertedAt = DateTime.UtcNow;
                _logger.LogWarning(
                    "SP poll {PollId} for system {SystemId} has non-decodable id and no lastOperationTime; using import time as inserted_at.",
                    poll.Id, systemId);
            }

            var pollId = await _pollRepository.CreateAsync(systemId, new CreatePollCommand(title, desc, type, timeEnd, insertedAt), ct);
            if (pollId is null)
                continue;

            var (data, skippedVotes) = BuildPollData(poll.Content, alterAssociations);
            if (skippedVotes > 0)
            {
                _logger.LogWarning(
                    "Dropped {Count} unmappable votes from SP poll {PollId} in system {SystemId} during import.",
                    skippedVotes, poll.Id, systemId);
            }

            if (data.ValueKind != JsonValueKind.Undefined)
            {
                await _pollRepository.UpdateAsync(systemId, new UpdatePollCommand(
                    Id: pollId.Value,
                    Title: null,
                    Description: null,
                    TimeEnd: null,
                    HasTimeEnd: false,
                    Data: data
                ), ct);
            }
        }
    }

    private async Task ImportNotesAsync(
        HttpClient httpClient, string spSystemId, SystemId systemId,
        Dictionary<string, AlterId> alterAssociations, EncryptionKeyMaterial encryptionKey, CancellationToken ct)
    {
        foreach (var (spMemberId, alterId) in alterAssociations)
        {
            var notes = await FetchAsync<List<SpEntity<SpNoteContent>>>(httpClient, SpApiPaths.Notes(spSystemId, spMemberId), ct);
            if (notes is null || notes.Count == 0)
                continue;

            foreach (var note in notes)
            {
                var title = note.Content.Title ?? "Imported note";
                if (title.Length > 100) title = title[..100];
                if (string.IsNullOrWhiteSpace(title)) title = "Imported note";

                // SP's `date` is the real created-date when present; same silent-1970 trap as
                // alters when it's 0. Cascade Date -> ObjectId -> UtcNow + warn so we never
                // silently land on the epoch and operators see a trace of any degraded row.
                DateTimeOffset createdAt;
                if (note.Content.Date > 0)
                {
                    createdAt = UnixTimestampToDateTimeOffset(note.Content.Date);
                }
                else if (SpObjectId.TryDecodeTimestamp(note.Id, out var decodedNoteAt))
                {
                    createdAt = new DateTimeOffset(decodedNoteAt, TimeSpan.Zero);
                    _logger.LogWarning(
                        "SP note {SpNoteId} for system {SystemId} alter {AlterId} has no date; falling back to ObjectId-decoded creation timestamp.",
                        note.Id, systemId, alterId);
                }
                else
                {
                    createdAt = DateTimeOffset.UtcNow;
                    _logger.LogWarning(
                        "SP note {SpNoteId} for system {SystemId} alter {AlterId} has no date and non-decodable id; using import time as created date.",
                        note.Id, systemId, alterId);
                }

                // lastOperationTime is the right source for updated_at - it's SP's last-edit
                // timestamp, which is exactly what UpdatedAt means here.
                var updatedAt = UnixTimestampToDateTimeOffset(note.Content.LastOperationTime);

                var entryId = await _journalRepository.CreateAlterAsync(systemId, new CreateAlterJournalEntryCommand(
                    AlterId: alterId,
                    Title: title,
                    CreatedAt: createdAt
                ), ct);

                if (entryId is null)
                    continue;

                var content = PrepareNote(note.Content.Note, encryptionKey, out var successful);
                var color = ParseColor(note.Content.Color);

                if (!successful && color is null)
                    continue;

                await _journalRepository.UpdateAlterAsync(systemId, new UpdateAlterJournalEntryCommand(
                    EntryId: entryId.Value,
                    Title: null,
                    Content: content,
                    Color: HexColor.FromNullable(color),
                    UpdatedAt: updatedAt
                ), ct);
            }
        }
    }

    private static string? PrepareNote(string? content, EncryptionKeyMaterial encryptionKey, out bool successful)
    {
        if (string.IsNullOrEmpty(content))
        {
            successful = true;
            return content;
        }

        content = content.Length > MaxJournalContentLength
            ? content[..MaxJournalContentLength]
            : content;

        var encrypted = TryEncryptForClient(content, encryptionKey);
        if (encrypted is null)
        {
            successful = false;
            return null;
        }

        successful = true;
        return encrypted;
    }

    // Mirrors client encryptData: real AES-256-GCM with random IV, ciphertext and tag stored separately.
    // The single `.Value` unwrap on base64Key is at the crypto-primitive boundary, where
    // the base64-string form is the API contract of Convert.FromBase64String.
    private static string? TryEncryptForClient(string plaintext, EncryptionKeyMaterial base64Key)
    {
        try
        {
            var key = Convert.FromBase64String(base64Key.Value);
            if (key.Length != 32)
                return null;

            var iv = new byte[12];
            RandomNumberGenerator.Fill(iv);

            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var ciphertext = new byte[plainBytes.Length];
            var tag = new byte[16];

            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(iv, plainBytes, ciphertext, tag);

            return $"enc|{Convert.ToBase64String(iv)}|{Convert.ToBase64String(ciphertext)}|{Convert.ToBase64String(tag)}";
        }
        catch
        {
            return null;
        }
    }
    
    // Typed on both sides: recoveryCode arrives from ImportAsync unwrapped from a nullable
    // at the outer guard, and the tuple's second slot is EncryptionKeyMaterial? so the
    // caller assigns straight into a typed nullable local rather than encoding "no derived
    // key" through an empty-string sentinel + IsNullOrWhiteSpace gate.
    private async Task<(SpImportResult Result, EncryptionKeyMaterial? DerivedKey)> ValidateEncryptionKeyAsync(SystemId systemId, RecoveryCode recoveryCode, CancellationToken ct)
    {
        var state = await _encryptionStateRepository.GetAsync(systemId, ct);
        if (state is not { Initialized: true, KeyChecksum: { } keyChecksum, Salt: { } salt }
            || string.IsNullOrWhiteSpace(keyChecksum.Value))
            return (new SpImportResult(false, 0, ImportErrorCode.SpImportFailed, "Encryption is not initialized for this system."), null);

        var pepper = _authOptions.CurrentValue.EncryptionPepper;
        var key = EncryptionKey.DeriveKey(pepper, systemId, recoveryCode, salt);
        var checksum = EncryptionKey.DeriveChecksum(key);
        if (checksum != keyChecksum)
            return (new SpImportResult(false, 0, ImportErrorCode.SpImportFailed, "The provided encryption key is invalid."), null);

        return (new SpImportResult(true, 0), key);
    }

    /// <summary>
    /// Builds the JSON `data` blob for an imported poll in the client's confirmed schema
    /// (see <see cref="PollDataJson"/>): custom SP polls become choice-poll data
    /// (<c>choices</c> + <c>responses</c> keyed by <c>choice_id</c>), standard SP polls
    /// become vote-poll data (<c>responses</c> with yes/no/abstain/veto + <c>allow_veto</c>).
    /// Returns the blob plus the number of SP-side votes we refused to import because they
    /// referenced a voter or an option we don't know about (unmapped member uuid, blank id,
    /// blank vote string, non-standard vote value, or a custom vote that matches no option).
    /// The caller surfaces that count as a single warning per poll rather than spamming one
    /// log line per dropped vote.
    /// </summary>
    private static (JsonElement Data, int SkippedUnmappableVotes) BuildPollData(
        SpPollContent poll, Dictionary<string, AlterId> alterAssociations)
    {
        var skippedUnmappableVotes = 0;
        var responses = new List<PollDataResponse>();

        if (poll.Custom)
        {
            // SP custom-poll votes carry the chosen option's name in `vote`; mint stable
            // choice ids and translate name → choice_id. SP option colors have no slot in
            // the client's {id,name} choice shape and were never rendered — dropped.
            var choices = new List<PollDataChoice>();
            var choiceIdByName = new Dictionary<string, PollChoiceId>(StringComparer.Ordinal);
            foreach (var option in poll.Options ?? [])
            {
                var name = option.Name ?? "";
                var choiceId = new PollChoiceId(Guid.NewGuid().ToString());
                choices.Add(new PollDataChoice(choiceId, name));
                choiceIdByName.TryAdd(name, choiceId);
            }

            foreach (var vote in poll.Votes ?? [])
            {
                if (string.IsNullOrWhiteSpace(vote.Id) || string.IsNullOrWhiteSpace(vote.Vote)
                    || !alterAssociations.TryGetValue(vote.Id, out var alterId)
                    || !choiceIdByName.TryGetValue(vote.Vote, out var choiceId))
                {
                    skippedUnmappableVotes++;
                    continue;
                }

                responses.Add(new PollDataResponse(alterId, Vote: null, ChoiceId: choiceId, Comment: vote.Comment));
            }

            return (PollDataJson.ToJsonElement(new ChoicePollData(choices, responses)), skippedUnmappableVotes);
        }

        foreach (var vote in poll.Votes ?? [])
        {
            // A vote without a voter or an opinion is meaningless, and a vote value outside
            // yes/no/abstain/veto would make the client's poll deserialization throw — drop
            // the row entirely in either case.
            if (string.IsNullOrWhiteSpace(vote.Id)
                || PollDataJson.TryParseVoteValue(vote.Vote) is not { } voteValue
                || !alterAssociations.TryGetValue(vote.Id, out var alterId))
            {
                skippedUnmappableVotes++;
                continue;
            }

            responses.Add(new PollDataResponse(alterId, Vote: voteValue, ChoiceId: null, Comment: vote.Comment));
        }

        return (PollDataJson.ToJsonElement(new VotePollData(responses, poll.AllowVeto)), skippedUnmappableVotes);
    }

    private async Task DownloadAvatarsAsync(SystemId systemId, List<AvatarDownload> downloads, CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory.CreateClient(HttpClientNames.SimplyPlural);

        foreach (var download in downloads)
        {
            try
            {
                using var response = await httpClient.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    continue;

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                //TODO: We should ideally be coverting it to webp for consistency of other images

                if (download.Kind == AvatarKind.System)
                {
                    var localUrl = await _avatarStorage.SaveSystemAvatarAsync(download.SystemId, stream, cancellationToken);
                    await _accountRepository.UpdateAvatarAsync(download.SystemId, localUrl, AvatarSource.Local, cancellationToken);
                }
                else
                {
                    // localUrl is AvatarUrl (non-null); implicit widening to AvatarUrl? at the
                    // command payload boundary preserves the "avatar was captured locally" signal.
                    var localUrl = await _avatarStorage.SaveAlterAvatarAsync(download.SystemId, download.AlterId!.Value, stream, cancellationToken);

                    await _alterRepository.UpdateAsync(systemId, new UpdateAlterCommand(
                        AlterId: download.AlterId!.Value,
                        Name: null,
                        Description: null,
                        AvatarUrl: localUrl,
                        AvatarSource: AvatarSource.Local,
                        Color: null,
                        Pronouns: null,
                        SecurityLevel: null,
                        Fields: null,
                        ProxyName: null,
                        Alias: null,
                        Untracked: null,
                        Archived: null,
                        Pinned: null,
                        UpdatedAt: DateTimeOffset.UtcNow
                    ), cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download avatar (kind={Kind}, alter={AlterId}) in system {SystemId}",
                    download.Kind, download.AlterId, download.SystemId);
            }
        }
    }

    /// <summary>
    /// Decides whether the SP-side system avatar should be rehosted (queued in the returned
    /// <see cref="AvatarDownload"/>) or passed through directly via
    /// <see cref="IAccountRepository.UpdateAvatarAsync"/>. Returns <see langword="null"/>
    /// when no rehost is required (because there's no avatar, or because the URL was a
    /// passthrough we already wrote synchronously).
    /// </summary>
    private async Task<AvatarDownload?> ImportSystemAvatarAsync(
        SystemId systemId,
        SpSystemContent content,
        CancellationToken cancellationToken)
    {
        var avatarUuid = content.AvatarUuid;
        var rawAvatarUrl = content.AvatarUrl;
        var uid = content.Uid;

        if (!string.IsNullOrWhiteSpace(avatarUuid) && !string.IsNullOrWhiteSpace(uid))
        {
            // Always rehost the canonical SP CDN URL because SP is shutting down.
            return new AvatarDownload(SpCdnHosts.Avatar(uid, avatarUuid), systemId, AvatarKind.System, null);
        }

        if (string.IsNullOrWhiteSpace(rawAvatarUrl) || !Uri.IsWellFormedUriString(rawAvatarUrl, UriKind.Absolute))
        {
            return null;
        }

        if (IsSpCdnUrl(rawAvatarUrl))
        {
            return new AvatarDownload(rawAvatarUrl, systemId, AvatarKind.System, null);
        }

        // Non-SP URL → store as passthrough; nothing to download.
        await _accountRepository.UpdateAvatarAsync(systemId, new(rawAvatarUrl), AvatarSource.External, cancellationToken);
        return null;
    }

    /// <summary>
    /// True when <paramref name="url"/> is an absolute URL hosted on the Simply Plural CDN.
    /// Used to decide between rehost (SP CDN, will go away soon) and passthrough (everything else).
    /// </summary>
    internal static bool IsSpCdnUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        foreach (var host in SpCdnHosts.All)
        {
            if (url.StartsWith(host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }


    private static async Task<T?> FetchAsync<T>(HttpClient httpClient, string url, CancellationToken ct)
    {
        try
        {
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return default;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            // Use the source-generated JsonSerializer context when available for better performance and to avoid trimming issues.
            var typeInfoObj = SpJsonContext.Default.GetTypeInfo(typeof(T));
            if (typeInfoObj is System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typedInfo)
            {
                var result = await JsonSerializer.DeserializeAsync<T>(stream, typedInfo, ct).ConfigureAwait(false);
                return result;
            }

            // Fallback to runtime deserialization
            var fallback = await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct).ConfigureAwait(false);
            return fallback;
        }
        catch
        {
            return default;
        }
    }

    // 0 = string/text, 1 = color, 2 = date, 3 = month, 4 = year, 5 = month+year, 6 = timestamp, 7 = month+day
    // `supportMarkdown` is nullable on SpCustomFieldContent because SP's update300 migration
    // emits `null` for legacy fields (see model comment). SP defaults missing to true, so we
    // do the same: null/true -> "text" (markdown), explicit false -> "plaintext".
    private static FieldType MapFieldType(SpFieldType spType, bool? supportMarkdown) => spType switch
    {
        SpFieldType.Text => (supportMarkdown ?? true) ? FieldType.Text : FieldType.Plaintext,
        SpFieldType.Colour => FieldType.Colour,
        SpFieldType.Date => FieldType.Date,
        SpFieldType.Month => FieldType.Month,
        SpFieldType.Year => FieldType.Year,
        SpFieldType.MonthYear => FieldType.MonthYear,
        SpFieldType.Timestamp => FieldType.Timestamp,
        SpFieldType.MonthDay => FieldType.MonthDay,
        _ => throw new ArgumentOutOfRangeException(nameof(spType), spType, "Unknown SP field type")
    };

    private static VisibilityLevel MapSecurityLevel(bool isPrivate, bool preventTrusted) => (isPrivate, preventTrusted) switch
    {
        (false, _) => VisibilityLevel.Public,
        (true, false) => VisibilityLevel.TrustedOnly,
        (true, true) => VisibilityLevel.Private,
    };

    private static DateTimeOffset UnixTimestampToDateTimeOffset(long unixMilliseconds)
    {
        // Convert Unix epoch milliseconds to DateTimeOffset
        var epochTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTime = epochTime.AddMilliseconds(unixMilliseconds);
        return new DateTimeOffset(dateTime, TimeSpan.Zero);
    }

    /// <summary>
    /// Normalise a Simply Plural colour to the canonical <c>#RRGGBB</c> shape a
    /// <see cref="HexColor"/> will accept. SP historically stores both shapes
    /// interchangeably (bare <c>RRGGBB</c> and <c>#RRGGBB</c>) — we canonicalise on
    /// the way in so no bare-hex value ever reaches <see cref="HexColor.FromNullable"/>,
    /// which now rejects anything not well-formed. Delegates to
    /// <see cref="HexColor.Normalise"/>, which is the single source of truth shared
    /// with the one-shot HexColorFixupService that walks legacy DB rows.
    /// </summary>
    internal static string? ParseColor(string? color) => HexColor.Normalise(color);

    /// <summary>
    /// Pending rehost of a Simply Plural CDN avatar. <see cref="AlterId"/> is required for
    /// <see cref="AvatarKind.Alter"/> entries and unused for <see cref="AvatarKind.System"/>.
    /// </summary>
    private sealed record AvatarDownload(string Url, SystemId SystemId, AvatarKind Kind, AlterId? AlterId);

    private enum AvatarKind
    {
        System,
        Alter,
    }
}
