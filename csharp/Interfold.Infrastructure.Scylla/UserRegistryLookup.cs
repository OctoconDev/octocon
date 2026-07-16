using System.Diagnostics.CodeAnalysis;
using Interfold.Contracts.Enums;

namespace Interfold.Infrastructure.Scylla;

/// <summary>
/// Which <c>user_registry</c> column a raw handle should route to. The wire prefixes
/// map 1:1 onto this enum via <see cref="UserRegistryLookup.TryParse"/> and every
/// read-side dispatch site inside the Scylla region-context switches on this value
/// instead of comparing prefix strings.
/// </summary>
internal enum UserRegistryLookupKind
{
    /// <summary>Region-scoped principal id, e.g. <c>"nam:abcdefg"</c>.</summary>
    Region,

    /// <summary>Username lookup, e.g. <c>"username:alice"</c>.</summary>
    Username,

    /// <summary>Discord snowflake lookup, e.g. <c>"discord:1234567"</c>.</summary>
    Discord,

    /// <summary>
    /// Bare 7-char system id or explicit <c>"id:"</c> prefix; routed to
    /// <c>user_registry.user_id</c>. The two shapes intentionally converge on the same
    /// registry column — <c>Kind.Id</c> is the strict "this string is a system id, not a
    /// username or a Discord snowflake" assertion.
    /// </summary>
    Id,
}

/// <summary>
/// A pre-resolution handle to a <c>user_registry</c> row, parsed from a raw
/// client-supplied string. Distinct from
/// <see cref="Interfold.Contracts.Ids.ScopedSystemId"/> (which is a post-resolution
/// principal id) and from <see cref="Interfold.Contracts.Ids.FriendLookup"/> (the
/// tighter route-binding type for the friend-request path, restricted to
/// Id / Username shapes).
///
/// <para>
/// The one remaining consumer of the four-kind universe (Region / Username /
/// Discord / Id) is <see cref="ScyllaUserRegistryRegionContext.HandleForLookup"/> /
/// <c>LookupAsync</c>, which routes to the right <c>user_registry</c> column. That
/// scope is Scylla-only, hence the internal accessibility and the residence inside
/// <c>Interfold.Infrastructure.Scylla</c>.
/// </para>
///
/// <para>
/// <b>Strict rejection of unknown non-region prefixes.</b> An input like
/// <c>"xxx:abcdefg"</c> (where <c>xxx</c> is neither a region tag nor one of the
/// four discriminator prefixes) returns <see langword="false"/> from
/// <see cref="TryParse"/>. Callers with a real registry
/// (<c>user_registry.user_id</c>) are expected to treat "not parseable" as "opaque
/// bare id" and query the registry with the <b>whole</b> input (not the
/// after-colon slice) — the miss surfaces as an unresolved lookup.
/// </para>
///
/// <para>
/// Behaviour is identical to the pre-merge <c>Interfold.Contracts.Ids.LookupHandle</c>;
/// only the name and scope have moved. The friend-request path used to share this
/// four-kind universe and was tightened to the two-kind <c>FriendLookup</c> at the
/// route boundary. Region-context queries stayed on the full universe because they
/// legitimately need every routing lane the <c>user_registry</c> exposes.
/// </para>
/// </summary>
internal readonly record struct UserRegistryLookup
{
    /// <summary>Which registry column the handle routes to.</summary>
    public UserRegistryLookupKind Kind { get; }

    /// <summary>
    /// The after-prefix content used as the actual lookup value. For bare inputs
    /// (<see cref="UserRegistryLookupKind.Id"/> without an explicit prefix) this is the
    /// whole input; for prefixed inputs it is the substring after the first colon.
    /// </summary>
    public string RawId { get; }

    /// <summary>The original untouched input string, retained for logging / audit.</summary>
    public string OriginalValue { get; }

    private UserRegistryLookup(UserRegistryLookupKind kind, string rawId, string originalValue)
    {
        Kind = kind;
        RawId = rawId;
        OriginalValue = originalValue;
    }

    /// <summary>
    /// Parse a raw handle string. Returns <see langword="false"/> (never throws) for
    /// null / blank / bare-prefix (<c>"nam:"</c>) / unknown-prefix (<c>"xxx:abcdefg"</c>)
    /// inputs. See the type-level xml-doc for the full behaviour matrix.
    /// </summary>
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
            // No colon → bare 7-char system id shape.
            handle = new UserRegistryLookup(UserRegistryLookupKind.Id, input, input);
            return true;
        }

        // Colon present but nothing before or after → not a valid prefixed handle. We
        // reject rather than fall back so the caller sees the input for what it is (a
        // client bug) rather than silently treating ":foo" as bare id "foo".
        if (separator == 0 || separator >= input.Length - 1)
        {
            return false;
        }

        var prefix = input[..separator];
        var afterColon = input[(separator + 1)..];

        // Region prefixes take priority — they're the canonical scoped-id shape.
        // EnumWire<ScyllaKeyspace>.TryParse mirrors ScopedSystemId.TryParseRegion
        // (case-insensitive, whitespace-trimmed, only the seven canonical wire tags
        // succeed) and is public across the assembly boundary.
        if (EnumWire<Contracts.Enums.ScyllaKeyspace>.TryParse(prefix, out _))
        {
            handle = new UserRegistryLookup(UserRegistryLookupKind.Region, afterColon, input);
            return true;
        }

        // Discriminator prefixes are lowercase-canonical on the wire; normalise for a
        // case-insensitive client while still rejecting anything not on the allow-list.
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
                // Unknown non-region prefix. Return false so callers know to fall back to
                // the "opaque bare id" path with the WHOLE input, matching the
                // strict-rejection contract described in the type xml-doc.
                return false;
        }
    }
}
