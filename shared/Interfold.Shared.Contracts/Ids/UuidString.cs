namespace Interfold.Shared.Contracts.Ids;

/// <summary>Parse helper for compact-N-canonical wrapper ids: accepts both <c>"N"</c> and
/// hyphenated GUID spellings.</summary>
public static class UuidString
{
    public static bool TryParse(string value, out Guid guid)
    {
        if (Guid.TryParseExact(value, "N", out guid))
        {
            return true;
        }

        return Guid.TryParse(value, out guid);
    }
}
