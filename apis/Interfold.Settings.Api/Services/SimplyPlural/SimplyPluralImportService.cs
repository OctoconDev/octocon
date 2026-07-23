using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Interfold.Api.Services.SimplyPlural;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Domain;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.ImportJobs;
using Interfold.Shared.Domain.Abstractions.Repository;
using Microsoft.Extensions.Options;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Models.ImportOperations;

namespace Interfold.Api.Services;

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
    private readonly TimeProvider _timeProvider;
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
        ILogger<SimplyPluralImportService> logger,
        IEncryptionStateRepository encryptionStateRepository,
        IOptionsMonitor<AuthenticationConfiguration> authConfig,
        TimeProvider timeProvider)
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
        _timeProvider = timeProvider;
    }

    public bool? WaitForAvatars { get; set; }
    
    public async Task<ImportJobOutcome> ImportAsync(
        SystemId systemId,
        ImportToken spToken,
        RecoveryCode? recoveryKey,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Simply Plural import for system {SystemId}", systemId);

        // Null = no derived key, matched at the gate below.
        EncryptionKeyMaterial? encryptionKey = null;
        if (recoveryKey is { } providedRecoveryKey && !string.IsNullOrWhiteSpace(providedRecoveryKey.Value))
        {
            var (encryptionValidation, derivedKey) = await ValidateEncryptionKeyAsync(systemId, providedRecoveryKey, cancellationToken);
            if (!encryptionValidation.Success)
                return encryptionValidation;

            encryptionKey = derivedKey;
        }

        using var httpClient = _httpClientFactory.CreateClient(HttpClientNames.SimplyPlural);
        // SP uses non-standard "Authorization: {token}" (no scheme prefix).
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", spToken.Value);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Interfold/spimport");

        var systemData = await FetchAsync<SpEntity<SpSystemContent>>(httpClient, SpApiPaths.Me(), cancellationToken);
        if (systemData is null)
            return new ImportJobOutcome(false, 0, ImportErrorCode.SpImportFailed, "Failed to fetch system data from Simply Plural.");

        var spSystemId = systemData.Id;
        var description = systemData.Content.Desc;

        var (fieldMapping, createdFieldIds) = await ImportCustomFieldsAsync(httpClient, spSystemId, systemId, cancellationToken);

        var (alterCount, alterAssociations, avatarDownloads) =
            await ImportAltersAsync(httpClient, spSystemId, systemId, fieldMapping, cancellationToken);

        var tagAssociations = await ImportTagsAsync(httpClient, spSystemId, systemId, alterAssociations, cancellationToken);

        await ImportFrontsAsync(httpClient, spSystemId, systemId, alterAssociations, cancellationToken);

        await ImportPollsAsync(httpClient, spSystemId, systemId, alterAssociations, cancellationToken);

        if (encryptionKey is { } derivedEncryptionKey)
        {
            await ImportNotesAsync(httpClient, spSystemId, systemId, alterAssociations, derivedEncryptionKey, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            var truncated = description.Length > 3000 ? description[..3000] : description;
            await _accountRepository.UpdateDescriptionAsync(systemId, truncated, cancellationToken);
        }

        // SP CDN avatars are queued for rehost (SP is shutting down); non-CDN URLs are
        // written through with avatar_source=External so they're visible immediately.
        var systemAvatarRehost = await ImportSystemAvatarAsync(systemId, systemData.Content, cancellationToken);
        if (systemAvatarRehost is not null)
        {
            avatarDownloads.Add(systemAvatarRehost);
        }

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
        return new ImportJobOutcome(true, alterCount);
    }

    private async Task<(Dictionary<string, FieldId> FieldMapping, List<FieldId> CreatedFieldIds)> ImportCustomFieldsAsync(
        HttpClient httpClient, string spSystemId, SystemId systemId, CancellationToken ct)
    {
        var fieldMapping = new Dictionary<string, FieldId>();
        var createdFieldIds = new List<FieldId>();

        var customFields = await FetchAsync<List<SpEntity<SpCustomFieldContent>>>(httpClient, SpApiPaths.CustomFields(spSystemId), ct);
        if (customFields is null)
            return (fieldMapping, createdFieldIds);

        foreach (var field in customFields)
        {
            var spFieldId = field.Id;

            // SP custom fields carry no timestamps; only signal is the MongoDB ObjectId's
            // first 4 bytes. Non-ObjectId ids skip rather than falling back to UtcNow.
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
        var alterAssociations = new Dictionary<string, AlterId>();
        var avatarDownloads = new List<AvatarDownload>();
        var alterCount = 0;

        var members = await FetchAsync<List<SpEntity<SpMemberContent>>>(httpClient, SpApiPaths.Members(spSystemId), ct);
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

            // Cascade date → ObjectId-encoded timestamp → UtcNow+warn. Never silently
            // stamp epoch; alter creation can't be skipped (downstream lookups by uuid).
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
                createdAt = _timeProvider.GetUtcNow();
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

            var pronouns = content.Pronouns;
            if (pronouns?.Length > 50) pronouns = pronouns[..50];

            var desc = content.Desc;
            if (desc?.Length > 3000) desc = desc[..3000];

            var color = ParseColor(content.Color);

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

            // Branches: (avatarUuid+uid) or SP CDN url → rehost; other absolute url →
            // passthrough (avatar_source=External); else no avatar.
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

            var updateCommand = new UpdateAlterCommand
            {
                AlterId = alterId.Value,
                Description = desc,
                AvatarUrl = AvatarUrl.FromNullable(passthroughUrl),
                AvatarSource = passthroughUrl is null ? null : AvatarSource.External,
                Color = HexColor.FromNullable(color),
                Pronouns = pronouns,
                SecurityLevel = securityLevel,
                Fields = fields,
                Untracked = isCustomFront,
                Archived = content.Archived,
                Pinned = false,
                UpdatedAt = updatedAt,
            };

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
        var tagAssociations = new Dictionary<string, TagId>();

        var groups = await FetchAsync<List<SpEntity<SpGroupContent>>>(httpClient, SpApiPaths.Groups(spSystemId), ct);
        if (groups is null)
            return tagAssociations;

        // Pass 1: create tags (no parent). Pass 2: parents. Pass 3: alter attachments.
        var tagEntries = new List<(string SpId, SpGroupContent Content)>();
        foreach (var group in groups)
        {
            tagEntries.Add((group.Id, group.Content));
        }

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
        // SP epoch: 2015-01-01. Chunk by 6 months to bound each API call.
        const long startEpoch = 1_420_070_400_000;
        const int monthInterval = 6;
        var chunkSizeMs = (long)monthInterval * 30 * 24 * 60 * 60 * 1000;
        var endTimeMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
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

                    var comment = front.Content.CustomStatus;
                    if (comment?.Length > 50) comment = comment[..50];

                    var spStart = DateTimeOffset.FromUnixTimeMilliseconds(front.Content.StartTime);
                    var spEnd = DateTimeOffset.FromUnixTimeMilliseconds(front.Content.EndTime);

                    await _frontingRepository.StartAsync(systemId, alterId, comment, spStart, ct);
                    await _frontingRepository.EndAsync(systemId, alterId, spEnd, ct);
                }
            }

            await Task.Delay(200, ct);
        }

        var currentFronters = await FetchAsync<List<SpEntity<SpFrontContent>>>(httpClient, SpApiPaths.CurrentFronters(), ct);
        if (currentFronters is not null)
        {
            foreach (var fronter in currentFronters)
            {
                var memberId = fronter.Content.Member;
                if (memberId is null || !alterAssociations.TryGetValue(memberId, out var alterId))
                    continue;

                var comment = fronter.Content.CustomStatus;
                if (comment?.Length > 50) comment = comment[..50];

                // Prefer startTime, else ObjectId-encoded creation second. Never fall back
                // to UtcNow (the original "today date" bug).
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
            // SP marks options optional even for custom polls; skip so we don't create
            // zero-choice rows that can't be voted on.
            if (poll.Content.Custom && (poll.Content.Options is null || poll.Content.Options.Count == 0))
            {
                _logger.LogWarning(
                    "Skipping custom SP poll {PollId} for system {SystemId}: custom poll has no options.",
                    poll.Id, systemId);
                continue;
            }

            // Match CreatePollCommandHandler caps (title <= 100, desc <= 2000).
            var title = poll.Content.Name ?? "Unnamed poll";
            if (title.Length > 100) title = title[..100];
            if (string.IsNullOrWhiteSpace(title)) title = "Unnamed poll";

            var desc = poll.Content.Desc;
            if (desc?.Length > 2000) desc = desc[..2000];

            var type = poll.Content.Custom ? PollType.Choice : PollType.Vote;

            DateTime? timeEnd = poll.Content.EndTime > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(poll.Content.EndTime).UtcDateTime
                : null;

            // Prefer ObjectId (created-second); lastOperationTime drifts on every edit.
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
                insertedAt = _timeProvider.GetUtcNow().UtcDateTime;
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

                // Cascade Date → ObjectId → UtcNow+warn (same silent-1970 trap as alters).
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
                    createdAt = _timeProvider.GetUtcNow();
                    _logger.LogWarning(
                        "SP note {SpNoteId} for system {SystemId} alter {AlterId} has no date and non-decodable id; using import time as created date.",
                        note.Id, systemId, alterId);
                }

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

    // Mirrors client encryptData: AES-256-GCM with random IV; ciphertext + tag stored separately.
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

    private async Task<(ImportJobOutcome Result, EncryptionKeyMaterial? DerivedKey)> ValidateEncryptionKeyAsync(SystemId systemId, RecoveryCode recoveryCode, CancellationToken ct)
    {
        var state = await _encryptionStateRepository.GetAsync(systemId, ct);
        if (state is not { Initialized: true, KeyChecksum: { } keyChecksum, Salt: { } salt }
            || string.IsNullOrWhiteSpace(keyChecksum.Value))
            return (new ImportJobOutcome(false, 0, ImportErrorCode.SpImportFailed, "Encryption is not initialized for this system."), null);

        var pepper = _authOptions.CurrentValue.EncryptionPepper;
        var key = EncryptionKey.DeriveKey(pepper, systemId, recoveryCode, salt);
        var checksum = EncryptionKey.DeriveChecksum(key);
        if (checksum != keyChecksum)
            return (new ImportJobOutcome(false, 0, ImportErrorCode.SpImportFailed, "The provided encryption key is invalid."), null);

        return (new ImportJobOutcome(true, 0), key);
    }

    /// <summary>Builds the client-schema poll data blob (see <see cref="PollDataJson"/>)
    /// and returns the count of unmappable votes (unknown voter, blank id, non-standard
    /// value, or custom vote matching no option) so the caller can log one warning per
    /// poll rather than one per dropped vote.</summary>
    private static (JsonElement Data, int SkippedUnmappableVotes) BuildPollData(
        SpPollContent poll, Dictionary<string, AlterId> alterAssociations)
    {
        var skippedUnmappableVotes = 0;
        var responses = new List<PollDataResponse>();

        if (poll.Custom)
        {
            // SP custom-poll votes carry option name; mint choice ids and translate name→id.
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
            // Drop votes with no voter, no opinion, or a non-standard vote value.
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

                if (download.Kind == AvatarKind.System)
                {
                    var localUrl = await _avatarStorage.SaveSystemAvatarAsync(download.SystemId, stream, cancellationToken);
                    await _accountRepository.UpdateAvatarAsync(download.SystemId, localUrl, AvatarSource.Local, cancellationToken);
                }
                else
                {
                    var localUrl = await _avatarStorage.SaveAlterAvatarAsync(download.SystemId, download.AlterId!.Value, stream, cancellationToken);

                    await _alterRepository.UpdateAsync(systemId, new UpdateAlterCommand
                    {
                        AlterId = download.AlterId!.Value,
                        AvatarUrl = localUrl,
                        AvatarSource = AvatarSource.Local,
                        UpdatedAt = _timeProvider.GetUtcNow(),
                    }, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download avatar (kind={Kind}, alter={AlterId}) in system {SystemId}",
                    download.Kind, download.AlterId, download.SystemId);
            }
        }
    }

    /// <summary>Returns a queued rehost download for SP CDN URLs, else writes a passthrough
    /// via <see cref="IAccountRepository.UpdateAvatarAsync"/>. Null when no rehost needed.</summary>
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

        await _accountRepository.UpdateAvatarAsync(systemId, new(rawAvatarUrl), AvatarSource.External, cancellationToken);
        return null;
    }

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

            // Prefer source-generated JsonTypeInfo (trim-safe); fall back to reflection.
            var typeInfoObj = SpJsonContext.Default.GetTypeInfo(typeof(T));
            if (typeInfoObj is System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typedInfo)
            {
                var result = await JsonSerializer.DeserializeAsync<T>(stream, typedInfo, ct).ConfigureAwait(false);
                return result;
            }

            var fallback = await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct).ConfigureAwait(false);
            return fallback;
        }
        catch
        {
            return default;
        }
    }

    // SP's update300 migration emits null supportMarkdown for legacy fields; SP defaults
    // to true, so null/true → Text (markdown), false → Plaintext.
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
        var epochTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTime = epochTime.AddMilliseconds(unixMilliseconds);
        return new DateTimeOffset(dateTime, TimeSpan.Zero);
    }

    /// <summary>Canonicalise SP's mixed bare/hash hex forms via
    /// <see cref="HexColor.Normalise"/> so <see cref="HexColor.FromNullable"/> never sees
    /// a non-well-formed value.</summary>
    internal static string? ParseColor(string? color) => HexColor.Normalise(color);

    /// <summary>Pending rehost of an SP CDN avatar. <see cref="AlterId"/> required for
    /// <see cref="AvatarKind.Alter"/>, unused for <see cref="AvatarKind.System"/>.</summary>
    private sealed record AvatarDownload(string Url, SystemId SystemId, AvatarKind Kind, AlterId? AlterId);

    private enum AvatarKind
    {
        System,
        Alter,
    }
}
