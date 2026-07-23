using System.Globalization;

namespace Interfold.Api.Services.SimplyPlural;

internal static class SpObjectId
{
    public static bool TryDecodeTimestamp(string? id, out DateTime utc)
    {
        utc = default;

        if (string.IsNullOrEmpty(id) || id.Length != 24)
        {
            return false;
        }

        // First 4 bytes (8 hex chars) are a big-endian Unix-seconds timestamp.
        // We also require the remaining 16 hex chars to look like hex so we don't
        // happily accept "12345678" + 16 chars of garbage.
        var timestampHex = id.AsSpan(0, 8);
        if (!uint.TryParse(timestampHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        for (var i = 8; i < 24; i++)
        {
            if (!IsHex(id[i]))
            {
                return false;
            }
        }

        utc = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        return true;
    }

    private static bool IsHex(char c) =>
        c is >= '0' and <= '9'
        or >= 'a' and <= 'f'
        or >= 'A' and <= 'F';
}
