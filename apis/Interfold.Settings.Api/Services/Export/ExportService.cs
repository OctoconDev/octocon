using Interfold.Alters.Contracts.Models;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Fronting.Contracts.Models.Read;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Polls.Contracts.Models.Read;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Tags.Contracts.Models.Read;
using Interfold.Tags.Domain.Abstractions.Repository;

namespace Interfold.Settings.Api.Services.Export;

// Composes the six repositories that back the two export shapes. Structural mirror of
// Octocon.Accounts.{gather_export_data, format_pk_export, format_full_export} —
// octocon/lib/octocon/accounts.ex:1179-1415. Ordering / byte-truncation /
// color-strip semantics live in ExportHelpers.
public sealed class ExportService : IExportService
{
    private readonly IAccountRepository _accounts;
    private readonly IAlterRepository _alters;
    private readonly ITagRepository _tags;
    private readonly IPollRepository _polls;
    private readonly IFrontingRepository _fronts;
    private readonly ISettingsFieldRepository _fields;

    public ExportService(
        IAccountRepository accounts,
        IAlterRepository alters,
        ITagRepository tags,
        IPollRepository polls,
        IFrontingRepository fronts,
        ISettingsFieldRepository fields)
    {
        _accounts = accounts;
        _alters = alters;
        _tags = tags;
        _polls = polls;
        _fronts = fronts;
        _fields = fields;
    }

    public async Task<PkExportPayload> BuildPkAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var profileTask = _accounts.GetPublicProfileAsync(systemId, cancellationToken);
        var altersTask = _alters.ListAsync(systemId, cancellationToken);
        var tagsTask = _tags.ListAsync(systemId, cancellationToken);
        await Task.WhenAll(profileTask, altersTask, tagsTask);

        var profile = await profileTask;
        var alters = await altersTask;
        var tags = await tagsTask;

        var members = alters.Select(BuildPkMember).ToArray();
        var groups = tags.Select((tag, index) => BuildPkGroup(tag, index)).ToArray();

        return new PkExportPayload(
            Version: 2,
            Name: profile?.Username?.Value,
            Description: ExportHelpers.ByteTruncateUtf8(profile?.Description, 1000),
            AvatarUrl: profile?.AvatarUrl?.Value,
            Switches: Array.Empty<object>(),
            Members: members,
            Groups: groups);
    }

    public async Task<FullExportPayload> BuildFullAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var profileTask = _accounts.GetPublicProfileAsync(systemId, cancellationToken);
        var altersTask = _alters.ListAsync(systemId, cancellationToken);
        var tagsTask = _tags.ListAsync(systemId, cancellationToken);
        var pollsTask = _polls.ListAsync(systemId, cancellationToken);
        var frontsTask = _fronts.ListAllAsync(systemId, cancellationToken);
        var fieldsTask = _fields.ListAsync(systemId, cancellationToken);
        await Task.WhenAll(profileTask, altersTask, tagsTask, pollsTask, frontsTask, fieldsTask);

        var profile = await profileTask;
        var alters = await altersTask;
        var tags = await tagsTask;
        var polls = await pollsTask;
        var fronts = await frontsTask;
        var fields = await fieldsTask;

        var user = new FullExportUser(
            Username: profile?.Username?.Value,
            Description: profile?.Description,
            Id: systemId.Value,
            AvatarUrl: profile?.AvatarUrl?.Value,
            Fields: fields.Select(f => new FullExportUserField(f.Id.Value, f.Name, f.Type, f.Locked, f.SecurityLevel)).ToArray());

        var alterEntries = alters.Select(BuildFullAlter).ToArray();
        var frontEntries = fronts.Select(BuildFullFront).ToArray();
        var tagEntries = tags.Select(BuildFullTag).ToArray();
        var pollEntries = polls.Select(BuildFullPoll).ToArray();

        return new FullExportPayload(user, alterEntries, frontEntries, tagEntries, pollEntries);
    }

    private static PkMember BuildPkMember(AlterReadModel alter)
    {
        var proxyTags = alter.DiscordProxies
            .Select(ExportHelpers.TrySplitProxyTag)
            .Where(t => t is not null)
            .Select(t => t!)
            .ToArray();

        return new PkMember(
            Id: alter.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Name: ExportHelpers.ByteTruncateUtf8(alter.Name, 100),
            Pronouns: ExportHelpers.ByteTruncateUtf8(alter.Pronouns, 100),
            Description: ExportHelpers.ByteTruncateUtf8(alter.Description, 1000),
            Color: ExportHelpers.FormatImportColor(alter.Color),
            AvatarUrl: alter.AvatarUrl?.Value,
            ProxyTags: proxyTags,
            DisplayName: alter.ProxyName);
    }

    private static PkGroup BuildPkGroup(TagReadModel tag, int index) => new(
        Id: index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Name: tag.Name,
        Description: tag.Description,
        Color: ExportHelpers.FormatImportColor(tag.Color),
        Members: tag.Alters
            .Select(a => a.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray());

    private static FullExportAlter BuildFullAlter(AlterReadModel alter) => new(
        Id: alter.Id.Value,
        Name: alter.Name,
        Pronouns: alter.Pronouns,
        Description: alter.Description,
        Color: alter.Color?.Value,
        AvatarUrl: alter.AvatarUrl?.Value,
        ProxyName: alter.ProxyName,
        DiscordProxies: alter.DiscordProxies,
        Fields: alter.Fields.Select(f => new FullExportAlterField(f.Id.Value, f.Value)).ToArray());

    private static FullExportFront BuildFullFront(FrontHistoryReadModel front) => new(
        Id: front.Id.Value,
        AlterId: front.AlterId.Value,
        Comment: front.Comment,
        TimeStart: front.TimeStart,
        TimeEnd: front.TimeEnd);

    private static FullExportTag BuildFullTag(TagReadModel tag) => new(
        Id: tag.Id.Value,
        Name: tag.Name,
        Description: tag.Description,
        Color: tag.Color?.Value,
        SecurityLevel: tag.SecurityLevel,
        ParentTagId: tag.ParentTagId?.Value,
        InsertedAt: tag.InsertedAt,
        UpdatedAt: tag.UpdatedAt,
        Alters: tag.Alters.Select(a => a.Value).ToArray());

    private static FullExportPoll BuildFullPoll(PollReadModel poll) => new(
        Id: poll.Id.Value,
        Title: poll.Title,
        Description: poll.Description,
        Type: poll.Type,
        Data: poll.Data,
        TimeEnd: poll.TimeEnd,
        InsertedAt: poll.InsertedAt,
        UpdatedAt: poll.UpdatedAt);
}
