using Interfold.Shared.Contracts.Ids;

namespace Interfold.Api.Socket;

/// <summary>
/// Typed form of the Phoenix <c>"system:{systemId}"</c> topic. Phoenix frames keep raw
/// string topics on the wire; this type owns the parse/build rule and the
/// region-prefix-tolerant system-id comparison that was previously duplicated across
/// <c>SocketPushContext</c> and <c>FriendshipSocketEventHandlers</c>.
/// </summary>
internal readonly record struct SystemTopic(SystemId Id)
{
    public const string Prefix = "system:";

    public string ToWireString() => $"{Prefix}{Id}";

    public static bool TryParse(string? topic, out SystemTopic result)
    {
        if (!string.IsNullOrWhiteSpace(topic)
            && topic.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && topic.Length > Prefix.Length)
        {
            result = new(new(topic[Prefix.Length..]));
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>
    /// Strips a legacy <c>"region:"</c> prefix (e.g. <c>nam:abcdefg</c> → <c>abcdefg</c>)
    /// so ids can be compared across prefixed and unprefixed spellings.
    /// </summary>
    public static string ComparableId(string systemId)
        => ScopedSystemId.StripRegionPrefix(systemId);

    /// <summary>Region-prefix-tolerant equality between two raw system-id spellings.</summary>
    public static bool IdMatches(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(ComparableId(left), ComparableId(right), StringComparison.Ordinal);
    }
}
