using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Ids;

/// <summary>Strongly-typed wrapper around a front id (Guid, wire form is 32-char lowercase hex).</summary>
[JsonConverter(typeof(FrontIdJsonConverter))]
public readonly record struct FrontId(Guid Value) : IParsable<FrontId>
{
    public static readonly FrontId Empty = new(Guid.Empty);

    public static explicit operator FrontId(Guid value) => new(value);
    public static implicit operator Guid(FrontId value) => value.Value;

    public override string ToString() => Value.ToString("N");

    public static FrontId Parse(string s, IFormatProvider? provider)
        => UuidString.TryParse(s, out var g)
            ? new(g)
            : throw new FormatException($"Value '{s}' is not a valid FrontId.");

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out FrontId result)
    {
        if (s is null || !UuidString.TryParse(s, out var g)) { result = default; return false; }
        result = new FrontId(g);
        return true;
    }

    public const int MaxCommentLength = 50;

    public static bool IsValidComment(string? comment) => (comment?.Length ?? 0) <= MaxCommentLength;
}

internal sealed class FrontIdJsonConverter : GuidIdJsonConverter<FrontId>
{
    protected override FrontId Create(Guid value) => new(value);
    protected override Guid GetValue(FrontId value) => value.Value;
    protected override string TypeLabel => nameof(FrontId);
}
