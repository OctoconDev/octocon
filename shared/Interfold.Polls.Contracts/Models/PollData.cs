using System.Text.Json;
using System.Text.Json.Serialization;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Polls.Contracts.Models;

// Server-side producer/mutator models for the poll `data` blob interior. Wire is
// two closed schemas keyed by poll type:
//   vote   → {"responses":[{"alter_id","vote","comment"}],"allow_veto":bool}
//   choice → {"choices":[{"id","name"}],"responses":[{"alter_id","choice_id","comment"}]}
// The transport contracts (PollReadModel.Data, UpdatePollCommand.Data) stay JsonElement
// so client blobs flow verbatim into persisted command JSON + idempotency hashes.

/// <summary>Choice-poll option id — opaque, distinct from PollId/TagId to prevent cross-wiring.</summary>
[JsonConverter(typeof(PollChoiceIdJsonConverter))]
public readonly record struct PollChoiceId
{
    public string Value { get; }

    public PollChoiceId(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override string ToString() => Value;
}

internal sealed class PollChoiceIdJsonConverter : StringBackedJsonConverter<PollChoiceId>
{
    protected override PollChoiceId Create(string value) => new(value);
    protected override string GetValue(PollChoiceId value) => value.Value;
}

public sealed record PollDataChoice(
    [property: JsonPropertyName("id")] PollChoiceId Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record PollDataResponse(
    [property: JsonPropertyName("alter_id")] AlterId AlterId,
    [property: JsonPropertyName("vote")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] VoteValue? Vote,
    [property: JsonPropertyName("choice_id")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PollChoiceId? ChoiceId,
    [property: JsonPropertyName("comment")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Comment);

/// <summary>The four vote spellings the client's <c>VoteType</c> enum accepts; producers
/// MUST drop unparseable votes or the client's poll-list deserialise throws.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VoteValue>))]
public enum VoteValue
{
    [JsonStringEnumMemberName("yes")]
    Yes,

    [JsonStringEnumMemberName("no")]
    No,

    [JsonStringEnumMemberName("abstain")]
    Abstain,

    [JsonStringEnumMemberName("veto")]
    Veto,
}

/// <summary>Vote-poll <c>data</c>. <c>allow_veto</c> is always written (server holds truth).</summary>
public sealed record VotePollData(
    [property: JsonPropertyName("responses")] IReadOnlyList<PollDataResponse> Responses,
    [property: JsonPropertyName("allow_veto")] bool AllowVeto);

/// <summary>The choice-poll <c>data</c> shape.</summary>
public sealed record ChoicePollData(
    [property: JsonPropertyName("choices")] IReadOnlyList<PollDataChoice> Choices,
    [property: JsonPropertyName("responses")] IReadOnlyList<PollDataResponse> Responses);

public static class PollDataJson
{
    /// <summary>Case-insensitive parse; null for null/empty/unknown so producers can drop.</summary>
    public static VoteValue? TryParseVoteValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.TryParseWire<VoteValue>(out var vote) ? vote : null;
    }

    public static JsonElement ToJsonElement<T>(T data)
        => JsonSerializer.SerializeToElement(data);

    /// <summary>Removes every top-level <c>responses[]</c> entry whose <c>alter_id</c>
    /// matches, preserving all other members (including unknown ones) verbatim and in
    /// order. Returns false when nothing changed.</summary>
    public static bool TryRemoveAlterResponses(JsonElement data, AlterId alterId, out JsonElement result)
    {
        result = data;
        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!data.TryGetProperty("responses", out var responses) || responses.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var removedAny = false;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in data.EnumerateObject())
            {
                if (!property.NameEquals("responses"))
                {
                    property.WriteTo(writer);
                    continue;
                }

                writer.WriteStartArray("responses");
                foreach (var entry in property.Value.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty("alter_id", out var entryAlterId)
                        && entryAlterId.ValueKind == JsonValueKind.Number
                        && entryAlterId.TryGetInt32(out var value)
                        && value == alterId.Value)
                    {
                        removedAny = true;
                        continue;
                    }

                    entry.WriteTo(writer);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }

        if (!removedAny)
        {
            return false;
        }

        result = JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
        return true;
    }
}
