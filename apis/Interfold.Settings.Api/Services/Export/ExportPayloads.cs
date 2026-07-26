using System.Text.Json;
using System.Text.Json.Serialization;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Settings.Api.Services.Export;

// Every [JsonPropertyName] key is hand-picked to mirror
// accounts.ex:1273-1415 byte-for-byte. No naming policy
// is applied at the serializer boundary (ExportJsonOptions) — the intent is that any
// forgotten key or typo surfaces immediately in the pinned-fixture tests rather than
// being papered over by SnakeCaseLower.
public sealed record PkExportPayload(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("switches")] IReadOnlyList<object> Switches,
    [property: JsonPropertyName("members")] IReadOnlyList<PkMember> Members,
    [property: JsonPropertyName("groups")] IReadOnlyList<PkGroup> Groups);

public sealed record PkMember(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("pronouns")] string Pronouns,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("color")] string? Color,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("proxy_tags")] IReadOnlyList<PkProxyTag> ProxyTags,
    [property: JsonPropertyName("display_name")] string? DisplayName);

public sealed record PkProxyTag(
    [property: JsonPropertyName("prefix")] string Prefix,
    [property: JsonPropertyName("suffix")] string Suffix);

public sealed record PkGroup(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("color")] string? Color,
    [property: JsonPropertyName("members")] IReadOnlyList<string> Members);

public sealed record FullExportPayload(
    [property: JsonPropertyName("user")] FullExportUser User,
    [property: JsonPropertyName("alters")] IReadOnlyList<FullExportAlter> Alters,
    [property: JsonPropertyName("fronts")] IReadOnlyList<FullExportFront> Fronts,
    [property: JsonPropertyName("tags")] IReadOnlyList<FullExportTag> Tags,
    [property: JsonPropertyName("polls")] IReadOnlyList<FullExportPoll> Polls);

public sealed record FullExportUser(
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("fields")] IReadOnlyList<FullExportUserField> Fields);

public sealed record FullExportUserField(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] FieldType Type,
    [property: JsonPropertyName("locked")] bool Locked,
    [property: JsonPropertyName("security_level")] VisibilityLevel SecurityLevel);

public sealed record FullExportAlter(
    [property: JsonPropertyName("id")] short Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("pronouns")] string? Pronouns,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("color")] string? Color,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("proxy_name")] string? ProxyName,
    [property: JsonPropertyName("discord_proxies")] IReadOnlyList<string> DiscordProxies,
    [property: JsonPropertyName("fields")] IReadOnlyList<FullExportAlterField> Fields);

public sealed record FullExportAlterField(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("value")] string? Value);

public sealed record FullExportFront(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("alter_id")] short AlterId,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("time_start")] DateTimeOffset TimeStart,
    [property: JsonPropertyName("time_end")] DateTimeOffset? TimeEnd);

public sealed record FullExportTag(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("color")] string? Color,
    [property: JsonPropertyName("security_level")] VisibilityLevel SecurityLevel,
    [property: JsonPropertyName("parent_tag_id")] Guid? ParentTagId,
    [property: JsonPropertyName("inserted_at")] DateTime InsertedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt,
    [property: JsonPropertyName("alters")] IReadOnlyList<short> Alters);

public sealed record FullExportPoll(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("type")] PollType Type,
    [property: JsonPropertyName("data")] JsonElement Data,
    [property: JsonPropertyName("time_end")] DateTime? TimeEnd,
    [property: JsonPropertyName("inserted_at")] DateTime InsertedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt);
