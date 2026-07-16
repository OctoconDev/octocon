using System.Text.Json;
using System.Text.Json.Serialization;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models;

/// <summary>
/// Typed model of the poll <c>data</c> blob's interior.
///
/// <para>
/// <b>Wire truth (confirmed against the Kotlin client, the only reader/writer of the
/// interior).</b> The blob has two closed schemas selected by the poll's <c>type</c>:
/// </para>
/// <list type="bullet">
///   <item>vote — <c>{"responses":[{"alter_id":&lt;int&gt;,"vote":"yes|no|abstain|veto","comment":&lt;string?&gt;}],"allow_veto":&lt;bool&gt;}</c></item>
///   <item>choice — <c>{"choices":[{"id":&lt;string&gt;,"name":&lt;string&gt;}],"responses":[{"alter_id":&lt;int&gt;,"choice_id":&lt;string&gt;,"comment":&lt;string?&gt;}]}</c></item>
/// </list>
///
/// <para>
/// The client parses with <c>ignoreUnknownKeys</c> and every member defaulted, so unknown
/// members are tolerated (historical Simply Plural imports wrote a legacy
/// <c>{"options","votes"}</c> shape the client ignores). The transport contracts
/// (<c>PollReadModel.Data</c>, <c>UpdatePollCommand.Data</c>) intentionally stay
/// <see cref="JsonElement"/>: client-authored blobs flow verbatim into persisted command
/// JSON and idempotency hashes, and a typed round-trip would normalise property order and
/// drop unknown members, invalidating stored hashes. These records are for server-side
/// producers/mutators only — <c>BuildPollData</c> (import) and
/// <c>RemoveAlterFromPollsAsync</c> (alter deletion).
/// </para>
/// </summary>
/// <summary>
/// A choice-poll option id — opaque to the client, distinct from <c>PollId</c>/<c>TagId</c>
/// so server-side mutators can't cross-wire them. JSON serializes as the raw string.
/// </summary>
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

internal sealed class PollChoiceIdJsonConverter : System.Text.Json.Serialization.JsonConverter<PollChoiceId>
{
    public override PollChoiceId Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(System.Text.Json.Utf8JsonWriter writer, PollChoiceId value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

public sealed record PollDataChoice(
    [property: JsonPropertyName("id")] PollChoiceId Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record PollDataResponse(
    [property: JsonPropertyName("alter_id")] AlterId AlterId,
    [property: JsonPropertyName("vote")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] VoteValue? Vote,
    [property: JsonPropertyName("choice_id")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PollChoiceId? ChoiceId,
    [property: JsonPropertyName("comment")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Comment);

/// <summary>
/// The four vote spellings the client's <c>VoteType</c> enum can parse — any other value in
/// a response would make the client's whole poll-list deserialization throw, so producers
/// must drop unparseable votes rather than pass them through.
/// </summary>
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

/// <summary>
/// The vote-poll <c>data</c> shape. <c>allow_veto</c> is always written because the client
/// declares it with a default but the server has the authoritative value.
/// </summary>
public sealed record VotePollData(
    [property: JsonPropertyName("responses")] IReadOnlyList<PollDataResponse> Responses,
    [property: JsonPropertyName("allow_veto")] bool AllowVeto);

/// <summary>The choice-poll <c>data</c> shape.</summary>
public sealed record ChoicePollData(
    [property: JsonPropertyName("choices")] IReadOnlyList<PollDataChoice> Choices,
    [property: JsonPropertyName("responses")] IReadOnlyList<PollDataResponse> Responses);

public static class PollDataJson
{
    /// <summary>
    /// Case-insensitive parse of a vote spelling. Returns null for null / empty / unknown
    /// values so producers can drop unparseable votes (see <see cref="VoteValue"/>).
    /// </summary>
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

    /// <summary>
    /// Removes every entry of the top-level <c>responses</c> array whose <c>alter_id</c>
    /// equals <paramref name="alterId"/>, preserving all other members (including unknown
    /// ones) verbatim and in order. Returns false when nothing changed.
    /// </summary>
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
