using System.Text;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Api.Services.Export;

// Reference parity helpers for the pk-side transforms in
// octocon/lib/octocon/accounts.ex:1273-1332.
internal static class ExportHelpers
{
    // Mirrors Elixir's `String.byte_slice(input, 0, maxBytes)`: clip on the UTF-8 byte
    // boundary WITHOUT splitting a codepoint. Null collapses to empty (matches
    // `(input || "") |> String.byte_slice`).
    public static string ByteTruncateUtf8(string? input, int maxBytes)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var utf8 = Encoding.UTF8;
        var byteCount = utf8.GetByteCount(input);
        if (byteCount <= maxBytes)
            return input;

        var bytes = utf8.GetBytes(input);
        var take = maxBytes;
        // Walk back until we're not sitting on a UTF-8 continuation byte (10xxxxxx) —
        // otherwise GetString would either throw or emit a replacement character.
        while (take > 0 && (bytes[take] & 0b1100_0000) == 0b1000_0000)
        {
            take--;
        }
        return utf8.GetString(bytes, 0, take);
    }

    // Mirrors Elixir's `format_import_color/1` (accounts.ex:1417): nil → nil, "#RRGGBB" → "RRGGBB",
    // anything else (short forms, alpha suffixes, missing '#') → nil so PK ignores the field.
    public static string? FormatImportColor(HexColor? color)
    {
        if (color is not { } value)
            return null;

        var raw = value.Value;
        // `byte_size(color) == 7` ⇒ "#" + 6 hex chars; hex validity is already enforced by
        // HexColor's ctor so a length check is sufficient here.
        if (raw.Length != 7 || raw[0] != '#')
            return null;

        return raw[1..];
    }

    // Split on the literal "text" sentinel (Elixir's `String.split(proxy |> trim, "text", parts: 2)`).
    // If "text" doesn't appear, the reference errors on the pattern-match; guard here so a bad
    // row can't 500 the entire export.
    public static PkProxyTag? TrySplitProxyTag(string proxy)
    {
        var trimmed = proxy.Trim();
        var idx = trimmed.IndexOf("text", StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var prefix = trimmed[..idx];
        var suffix = trimmed[(idx + "text".Length)..];
        return new PkProxyTag(
            ByteTruncateUtf8(prefix, 50),
            ByteTruncateUtf8(suffix, 50));
    }
}
