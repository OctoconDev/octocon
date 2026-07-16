namespace Interfold.Contracts.Ids;

/// <summary>
/// Parse helper for the string-backed entity ids (<see cref="TagId"/>, <see cref="EntryId"/>,
/// <see cref="PollId"/>, <see cref="FieldId"/>, <see cref="FrontId"/>) whose canonical wire
/// spelling is a compact (<c>"N"</c>) GUID but whose legacy rows/clients may carry the
/// hyphenated form. Replaces the byte-identical <c>TryParseUuid</c> copies that lived in
/// each repository.
/// </summary>
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
