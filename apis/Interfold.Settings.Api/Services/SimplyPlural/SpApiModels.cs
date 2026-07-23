using System.Text.Json.Serialization;

namespace Interfold.Api.Services.SimplyPlural;

// --- Wrapper: every SP API entity comes as { "id": "...", "content": { ... } } ---

internal sealed class SpEntity<TContent>
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public TContent Content { get; set; } = default!;
}

// --- /me ---

internal sealed class SpSystemContent
{
    [JsonPropertyName("desc")]
    public string? Desc { get; set; }

    [JsonPropertyName("avatarUuid")]
    public string? AvatarUuid { get; set; }

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("uid")]
    public string? Uid { get; set; }
}

// --- /customFields/{id} ---

/// <summary>
/// Simply Plural's numeric custom-field type codes (their v1 API `type` member).
/// Deserialized straight from the wire integers 0–7; unknown future codes land as
/// undefined enum values and are rejected by <c>MapFieldType</c>'s exhaustive switch.
/// </summary>
internal enum SpFieldType
{
    Text = 0,
    Colour = 1,
    Date = 2,
    Month = 3,
    Year = 4,
    MonthYear = 5,
    Timestamp = 6,
    MonthDay = 7,
}

internal sealed class SpCustomFieldContent
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public SpFieldType Type { get; set; }

    // SP's `update300.ts` migration copies legacy field schemas into the customFields
    // collection via `insertOne({ ..., supportMarkdown: field.supportMarkdown, ... })`
    // where the legacy `field.supportMarkdown` is undefined for pre-markdown rows.
    // Mongo stores undefined as `null`, the SP API serialises it as `"supportMarkdown":null`
    [JsonPropertyName("supportMarkdown")]
    public bool? SupportMarkdown { get; set; }

    [JsonPropertyName("private")]
    public bool Private { get; set; }

    [JsonPropertyName("preventTrusted")]
    public bool PreventTrusted { get; set; }
}

// --- /members/{id}  and  /customFronts/{id} ---

internal sealed class SpMemberContent
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("pronouns")]
    public string? Pronouns { get; set; }

    [JsonPropertyName("desc")]
    public string? Desc { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("avatarUuid")]
    public string? AvatarUuid { get; set; }

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    [JsonPropertyName("date")]
    public long Date { get; set; }

    [JsonPropertyName("lastOperationTime")]
    public long LastOperationTime { get; set; }

    [JsonPropertyName("info")]
    public Dictionary<string, string?>? Info { get; set; }

    [JsonPropertyName("archived")]
    public bool Archived { get; set; }

    [JsonPropertyName("private")]
    public bool Private { get; set; } = true;

    [JsonPropertyName("preventTrusted")]
    public bool PreventTrusted { get; set; } = true;
}

// --- /groups/{id} ---

internal sealed class SpGroupContent
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("desc")]
    public string? Desc { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

    [JsonPropertyName("members")]
    public List<string>? Members { get; set; }

    [JsonPropertyName("private")]
    public bool Private { get; set; } = true;

    [JsonPropertyName("preventTrusted")]
    public bool PreventTrusted { get; set; } = true;
}

// --- /frontHistory/{id}  and  /fronters/ ---

internal sealed class SpFrontContent
{
    [JsonPropertyName("member")]
    public string? Member { get; set; }

    [JsonPropertyName("startTime")]
    public long StartTime { get; set; }

    [JsonPropertyName("endTime")]
    public long EndTime { get; set; }

    [JsonPropertyName("customStatus")]
    public string? CustomStatus { get; set; }

    [JsonPropertyName("lastOperationTime")]
    public long LastOperationTime { get; set; }
}

// --- /polls/{id} ---

internal sealed class SpPollContent
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("desc")]
    public string? Desc { get; set; }

    [JsonPropertyName("custom")]
    public bool Custom { get; set; }

    [JsonPropertyName("endTime")]
    public long EndTime { get; set; }

    [JsonPropertyName("allowAbstain")]
    public bool AllowAbstain { get; set; }

    [JsonPropertyName("allowVeto")]
    public bool AllowVeto { get; set; }

    [JsonPropertyName("options")]
    public List<SpPollOption>? Options { get; set; }

    [JsonPropertyName("votes")]
    public List<SpPollVote>? Votes { get; set; }

    [JsonPropertyName("lastOperationTime")]
    public long LastOperationTime { get; set; }
}

internal sealed class SpPollOption
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }
}

internal sealed class SpPollVote
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("vote")]
    public string? Vote { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

// --- /notes/{systemId}/{memberId} ---

internal sealed class SpNoteContent
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("date")]
    public long Date { get; set; }

    [JsonPropertyName("member")]
    public string? Member { get; set; }

    // Defensively nullable: legacy notes from pre-supportMarkdown SP versions can serialise
    // as `null` for the same reason custom fields do (Mongo stores `undefined` -> SP API
    // emits `null`). We don't read this field today, but a non-nullable `bool` would make
    // the whole notes deserialisation throw and silently empty the per-alter notes page.
    [JsonPropertyName("supportMarkdown")]
    public bool? SupportMarkdown { get; set; }

    [JsonPropertyName("lastOperationTime")]
    public long LastOperationTime { get; set; }
}