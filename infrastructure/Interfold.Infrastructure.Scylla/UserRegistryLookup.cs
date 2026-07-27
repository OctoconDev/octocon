using System.Diagnostics.CodeAnalysis;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Infrastructure.Scylla;

/// <summary>Which <c>user_registry</c> column a parsed handle routes to. Wire prefixes
/// map 1:1 onto this enum via <see cref="UserRegistryLookup.TryParse"/>.</summary>
internal enum UserRegistryLookupKind
{
    /// <summary>Region-scoped principal id, e.g. <c>"nam:abcdefg"</c>.</summary>
    Region,

    /// <summary>Username lookup, e.g. <c>"username:alice"</c>.</summary>
    Username,

    /// <summary>Discord snowflake lookup, e.g. <c>"discord:1234567"</c>.</summary>
    Discord,

    /// <summary>Bare 7-char system id or explicit <c>"id:"</c> prefix; both route to
    /// <c>user_registry.user_id</c>. <c>Id</c> is the strict "this is a system id, not a
    /// username or Discord snowflake" assertion.</summary>
    Id,
}

/// <summary>Pre-resolution handle to a <c>user_registry</c> row, parsed from a raw
/// client-supplied string. Scylla-only — friend-request routing uses the tighter
/// <see cref="Interfold.Friendships.Contracts.Ids.FriendLookup"/>.
///
/// <para><b>Strict rejection of unknown non-region prefixes.</b> Inputs like
/// <c>"xxx:abcdefg"</c> return <see langword="false"/> from <see cref="TryParse"/>.
/// Callers must treat a rejected input as an opaque bare id and query the registry
/// with the <b>whole</b> string (not the after-colon slice).</para></summary>
internal readonly record struct UserRegistryLookup
{
    public UserRegistryLookupKind Kind { get; }

    /// <summary>Lookup value: after-colon slice for prefixed inputs, whole input for
    /// bare <see cref="UserRegistryLookupKind.Id"/>.</summary>
    public string RawId { get; }

    /// <summary>The untouched input, retained for logging / audit.</summary>
    public string OriginalValue { get; }

    private UserRegistryLookup(UserRegistryLookupKind kind, string rawId, string originalValue)
    {
        Kind = kind;
        RawId = rawId;
        OriginalValue = originalValue;
    }

    /// <summary>Parse a raw handle string. Never throws; returns <see langword="false"/>
    /// for null / blank / bare-prefix (<c>"nam:"</c>) / unknown-prefix inputs.</summary>
    public static bool TryParse([NotNullWhen(true)] string? input, out UserRegistryLookup handle)
    {
        handle = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var separator = input.IndexOf(':');
        if (separator < 0)
        {
            handle = new UserRegistryLookup(UserRegistryLookupKind.Id, input, input);
            return true;
        }

        // Reject ":foo" / "foo:" so the client sees a real error instead of a silent bare-id lookup.
        if (separator == 0 || separator >= input.Length - 1)
        {
            return false;
        }

        var prefix = input[..separator];
        var afterColon = input[(separator + 1)..];

        // Region prefixes win — they are the canonical scoped-id shape.
        if (EnumWire<Interfold.Shared.Contracts.Enums.ScyllaKeyspace>.TryParse(prefix, out _))
        {
            handle = new UserRegistryLookup(UserRegistryLookupKind.Region, afterColon, input);
            return true;
        }

        // Discriminator prefixes are wire-lowercase; case-insensitive client, strict allow-list.
        switch (prefix.ToLowerInvariant())
        {
            case "id":
                handle = new UserRegistryLookup(UserRegistryLookupKind.Id, afterColon, input);
                return true;
            case "username":
                handle = new UserRegistryLookup(UserRegistryLookupKind.Username, afterColon, input);
                return true;
            case "discord":
                handle = new UserRegistryLookup(UserRegistryLookupKind.Discord, afterColon, input);
                return true;
            default:
                return false;
        }
    }
}
