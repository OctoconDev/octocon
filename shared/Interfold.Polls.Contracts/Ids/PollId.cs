using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Ids;

/// <summary>Strongly-typed wrapper around a poll id (Guid, wire form is 32-char lowercase hex).</summary>
[JsonConverter(typeof(PollIdJsonConverter))]
public readonly record struct PollId(Guid Value) : IParsable<PollId>
{
    public static readonly PollId Empty = new(Guid.Empty);

    public static explicit operator PollId(Guid value) => new(value);
    public static implicit operator Guid(PollId value) => value.Value;

    public override string ToString() => Value.ToString("N");

    public static PollId Parse(string s, IFormatProvider? provider)
        => UuidString.TryParse(s, out var g)
            ? new(g)
            : throw new FormatException($"Value '{s}' is not a valid PollId.");

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out PollId result)
    {
        if (s is null || !UuidString.TryParse(s, out var g)) { result = default; return false; }
        result = new PollId(g);
        return true;
    }
}

internal sealed class PollIdJsonConverter : GuidIdJsonConverter<PollId>
{
    protected override PollId Create(Guid value) => new(value);
    protected override Guid GetValue(PollId value) => value.Value;
    protected override string TypeLabel => nameof(PollId);
}
