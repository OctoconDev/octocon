using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Interfold.Contracts.Enums;

namespace Interfold.Contracts.Ids;

/// <summary>
/// A <see cref="SystemId"/> guaranteed to be in the scoped <c>{region}:{rawId}</c> wire form.
/// Compile-time enforcement of the invariant that every persistence-adapter partition key,
/// every <c>InProcessEventBus</c> routing key, every JWT <c>sub</c>, and every Postgres
/// idempotency PK sees a scoped composite (never a raw or double-prefixed id).
///
/// <para>
/// <b>Idempotent by construction.</b> Both <see cref="Compose(ScyllaKeyspace, string)"/> and
/// <see cref="Compose(ScyllaKeyspace, SystemId)"/> strip any existing region prefix before
/// re-applying the resolved region, so passing in an already-scoped string produces the
/// exact same <see cref="Value"/> as passing in the bare id — the <c>"nam:nam:abcdefg"</c>
/// double-prefix footgun is impossible to construct through this type.
/// </para>
///
/// <para>
/// <b>Wire compatibility.</b> <see cref="Value"/> is byte-identical to the scoped-form string
/// every persistence layer already keys on, and the JSON converter writes it verbatim
/// (matches <c>SystemIdJsonConverter</c>).
/// </para>
///
/// <para>
/// <b>Not to be confused with</b> <see cref="FriendLookup"/> (which expresses "either a
/// system id OR a username" at route-binding time) or
/// <c>Interfold.Domain.FriendshipIdNormalization</c> (which re-applies the principal's
/// prefix rather than stripping — different semantics).
/// </para>
/// </summary>
[JsonConverter(typeof(ScopedSystemIdJsonConverter))]
public readonly record struct ScopedSystemId : IParsable<ScopedSystemId>
{
    /// <summary>
    /// The canonical wire form, e.g. <c>"nam:abcdefg"</c>. This is what
    /// <see cref="SystemId.Value"/> returns for a principal today, and what every persistence
    /// adapter binds as its row PK. Never null; never empty for a well-constructed value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// The bare id with the region prefix stripped, e.g. <c>"abcdefg"</c>. Scylla partition
    /// keys and the friendship-normalization "reapply principal's region" path read this.
    /// </summary>
    public string RawId { get; }

    /// <summary>The home region — matches the prefix on <see cref="Value"/>.</summary>
    public ScyllaKeyspace Region { get; }

    private ScopedSystemId(string value, string rawId, ScyllaKeyspace region)
    {
        Value = value;
        RawId = rawId;
        Region = region;
    }

    /// <summary>
    /// Compose from an explicitly-resolved region and a possibly-scoped id string. Any
    /// existing region prefix on <paramref name="maybeScoped"/> is stripped first, so calling
    /// <see cref="Compose(ScyllaKeyspace, string)"/> with either <c>"abcdefg"</c> or
    /// <c>"nam:abcdefg"</c> yields the same result when the region argument is
    /// <see cref="ScyllaKeyspace.Nam"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="maybeScoped"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="maybeScoped"/> is blank after strip.</exception>
    public static ScopedSystemId Compose(ScyllaKeyspace region, string maybeScoped)
    {
        ArgumentNullException.ThrowIfNull(maybeScoped);
        var raw = StripRegionPrefix(maybeScoped);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("Cannot compose a ScopedSystemId from a blank id.", nameof(maybeScoped));
        }

        var regionWire = region.ToWire();
        return new ScopedSystemId($"{regionWire}:{raw}", raw, region);
    }

    /// <summary>
    /// Compose from an explicitly-resolved region and a typed <see cref="SystemId"/>. Any
    /// existing prefix on <paramref name="systemId"/> is stripped first — the composer is
    /// idempotent whether the caller already carried a scoped value or a raw one.
    /// </summary>
    public static ScopedSystemId Compose(ScyllaKeyspace region, SystemId systemId)
        => Compose(region, systemId.Value);

    /// <summary>
    /// Compose from a string-typed wire-form region tag (e.g. what
    /// <c>IScyllaKeyspaceResolver.ResolveRegionalKeyspace</c> returns) and a possibly-scoped
    /// id. Behaves identically to <see cref="Compose(ScyllaKeyspace, string)"/> once the
    /// region is parsed; unknown region strings throw with a clear error, matching the
    /// contract of the enum overload's fail-fast on invalid input.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="regionWire"/> is not a recognised region tag.</exception>
    public static ScopedSystemId Compose(string regionWire, string maybeScoped)
    {
        if (!TryParseRegion(regionWire, out var region))
        {
            throw new ArgumentException(
                $"'{regionWire}' is not a recognised region wire tag (expected one of nam, eur, sam, sas, eas, ocn, gdpr).",
                nameof(regionWire));
        }
        return Compose(region, maybeScoped);
    }

    /// <summary>
    /// Parse a wire-form string that is expected to already carry a valid region prefix.
    /// Throws for unscoped, blank, or unknown-region input — the strict-parse path is the
    /// right choice at trust boundaries (JWT <c>sub</c>, incoming events) where a caller
    /// that lost the prefix indicates a real bug rather than a legacy shape to be tolerated.
    /// </summary>
    /// <exception cref="ArgumentException">Input is unscoped, blank, or the region tag is unknown.</exception>
    public static ScopedSystemId ParseScoped(string wire)
    {
        if (!TryParseScoped(wire, out var result))
        {
            throw new ArgumentException(
                $"'{wire}' is not a valid scoped system id (expected '<region>:<rawId>' with region in {{nam, eur, sam, sas, eas, ocn, gdpr}}).",
                nameof(wire));
        }
        return result;
    }

    /// <summary>
    /// Attempt to parse a wire-form string. Returns <c>false</c> (never throws) for null,
    /// empty, whitespace-only, unscoped, or unknown-region input. Used by every read-side
    /// site that needs to tolerate legacy shapes (route binding, InterfoldPrincipalMiddleware
    /// on legacy JWTs, the region-context lookup cache).
    /// </summary>
    public static bool TryParseScoped([NotNullWhen(true)] string? wire, out ScopedSystemId result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(wire))
        {
            return false;
        }

        var separator = wire.IndexOf(':');
        if (separator <= 0 || separator >= wire.Length - 1)
        {
            return false;
        }

        var regionRaw = wire[..separator];
        var raw = wire[(separator + 1)..];
        if (!TryParseRegion(regionRaw, out var region))
        {
            return false;
        }

        // Canonicalise the prefix casing on the way out — the ScyllaKeyspace wire values are
        // always lowercase and every read-side caller compares against the lowercase form.
        result = new ScopedSystemId($"{region.ToWire()}:{raw}", raw, region);
        return true;
    }

    /// <summary>Return this scoped id as an unmarked <see cref="SystemId"/> for wire-boundary consumers.</summary>
    public SystemId AsSystemId() => new(Value);

    /// <summary>
    /// Semantic "does <paramref name="candidate"/> refer to the same user as this principal?" —
    /// the correct primitive for controllers guarding against self-request / self-friendship /
    /// self-trust. A bare byte compare (<c>principal == candidate</c> via the implicit widen)
    /// catches the scoped <c>"nam:abcdefg"</c> shape but silently misses the raw
    /// <c>"abcdefg"</c> shape; this method canonicalises <paramref name="candidate"/> so raw
    /// and same-region-scoped inputs both self-reject.
    ///
    /// <para>
    /// <b>Cross-region shapes.</b> If <paramref name="candidate"/> carries its own region
    /// prefix (e.g. <c>"eur:abcdefg"</c>) it is compared byte-for-byte against the scoped
    /// composite — a cross-region id that happens to share this raw id is treated as a
    /// different user, matching the "scoped composite is identity" contract above.
    /// </para>
    ///
    /// <para>
    /// <b>Not to be confused with</b> <see cref="Compose(ScyllaKeyspace, SystemId)"/> which
    /// unconditionally re-applies this region — that shape is right for the
    /// <c>FriendshipIdNormalization.CanonicalizeForPrincipal</c> callers that WANT
    /// principal-region coercion, and wrong for the controller self-check where we must not
    /// coerce the client's <c>eur:abcdefg</c> into <c>nam:abcdefg</c> and falsely self-reject.
    /// </para>
    /// </summary>
    public bool RepresentsSameUserAs(SystemId candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Value))
        {
            return false;
        }

        // If the candidate carries its own region prefix, compare scoped-to-scoped.
        // The strict parse returns false for anything without a valid region tag, so a
        // discriminator-prefixed input like "username:alice" (which should never reach
        // this overload since it belongs to FriendLookup) also falls through to
        // the raw-id branch and yields "not self" because "alice" != this.RawId.
        if (TryParseScoped(candidate.Value, out var scopedCandidate))
        {
            return string.Equals(Value, scopedCandidate.Value, StringComparison.Ordinal);
        }

        // Bare / raw shape → treat as living in this principal's region. Every
        // /api/friends/{rawId} and /api/friend-requests/{rawId}/{action} route hits here.
        return string.Equals(RawId, candidate.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Route-binding overload for the friend-request send path where the route segment is
    /// bound as <see cref="FriendLookup"/>. Delegates to the
    /// <see cref="RepresentsSameUserAs(SystemId)"/> primitive when the candidate is an
    /// id shape (bare or explicit <c>id:</c> prefix). Username shapes always return
    /// <see langword="false"/> — a username requires a registry lookup to resolve to a
    /// concrete system id, so the fast-path can't decide self here and the downstream
    /// handler's post-resolution self-check takes over (see
    /// <c>SendFriendRequestCommandHandler</c>'s resolved-id self-guard). The fast-path
    /// keeps the crisp <c>cannot_send_self</c> error for the trivial "client sends their
    /// own id" case without a repository hop.
    ///
    /// <para>
    /// Every non-id/username shape (Discord, region-scoped, unknown-prefix, blank) is
    /// rejected by <see cref="FriendLookup.TryParse"/> at route binding with a 400, so
    /// this method never sees them — the pre-merge fall-through to a byte-level compare
    /// against the raw wire is gone (and unnecessary now that the wire contract is
    /// strict).
    /// </para>
    /// </summary>
    public bool RepresentsSameUserAs(FriendLookup candidate) => candidate.Kind switch
    {
        // Kind.Id — bare id or explicit "id:" prefix. FriendLookup.Value is the
        // after-prefix content, i.e. exactly the raw id the primitive expects; the
        // primitive's raw-id branch matches "this principal's RawId == candidate.Value".
        FriendLookupKind.Id => RepresentsSameUserAs(new SystemId(candidate.Value)),

        // Kind.Username — requires a registry hop; the downstream resolver's post-resolution
        // self-check catches "client sent their own username" without needing this overload
        // to guess.
        FriendLookupKind.Username => false,

        _ => false,
    };

    /// <summary>
    /// Implicit widen to <see cref="SystemId"/>. Persistence adapters, event constructors, and
    /// repository entry points still declare <see cref="SystemId"/> parameters (the "unmarked
    /// scoped composite" shape); the type is tightened at ingress
    /// (<c>CommandEnvelope.PrincipalId</c>, <c>ITargetedClusterEvent.TargetSystemId</c>)
    /// without forcing every downstream signature to change. The widen is byte-identical, so
    /// it does not alter any wire representation.
    /// </summary>
    public static implicit operator SystemId(ScopedSystemId scoped) => scoped.AsSystemId();

    public override string ToString() => Value;

    // --- IParsable — enables [FromRoute] ScopedSystemId systemId binding on controllers. ---
    // Route-bound values come from client-controlled URLs, so we use the strict-parse path:
    // an unscoped path segment surfaces as a 400 instead of silently defaulting.

    public static ScopedSystemId Parse(string s, IFormatProvider? provider) => ParseScoped(s);

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out ScopedSystemId result)
        => TryParseScoped(s, out result);

    // --- Public strip helpers (single source of truth for the region-prefix strip) ---

    /// <summary>
    /// Typed overload — strips the region prefix from the wrapped
    /// <see cref="SystemId.Value"/>. Delegates to the string overload; kept so callers
    /// holding a typed <see cref="SystemId"/> don't have to unwrap to <see cref="string"/>
    /// just to normalise.
    /// </summary>
    public static string StripRegionPrefix(SystemId systemId) => StripRegionPrefix(systemId.Value);

    /// <summary>
    /// Strip a leading <c>{region}:</c> prefix if present, e.g.
    /// <c>"nam:abcdefg"</c> → <c>"abcdefg"</c>. The strip fires only when the substring
    /// before the first colon is a recognised region tag (<c>nam</c>, <c>eur</c>,
    /// <c>sam</c>, <c>sas</c>, <c>eas</c>, <c>ocn</c>, <c>gdpr</c>); every other input
    /// (blank, no colon, colon-at-start, non-region prefix like <c>"username:"</c>) is
    /// returned unchanged. The strip is <b>single-pass</b> — a double-prefixed
    /// <c>"nam:nam:abcdefg"</c> yields <c>"nam:abcdefg"</c>, not the bare id; callers that
    /// need to canonicalise a suspected legacy double-prefix loop this call to a fixed
    /// point.
    ///
    /// <para>
    /// The friend-request discriminator prefixes (<c>username:</c>, <c>id:</c>) belong to
    /// <see cref="FriendLookup"/>, and the registry-column discriminator prefixes
    /// (<c>username:</c> / <c>discord:</c> / <c>id:</c>) belong to the internal Scylla
    /// <c>UserRegistryLookup</c> — refusing to strip them here keeps the parsers'
    /// responsibilities separate. A bare prefix like <c>"nam:"</c> deliberately returns
    /// empty so <see cref="Compose(ScyllaKeyspace, string)"/> surfaces the caller-bug via
    /// its blank-raw guard rather than emitting a malformed <c>"nam:nam:"</c>.
    /// </para>
    ///
    /// <para>
    /// Not to be confused with <c>Interfold.Domain.FriendshipIdNormalization</c>, which
    /// deliberately re-applies the principal's prefix — different semantics.
    /// </para>
    /// </summary>
    public static string StripRegionPrefix(string maybeScoped)
    {
        if (string.IsNullOrWhiteSpace(maybeScoped))
        {
            return maybeScoped;
        }

        var separator = maybeScoped.IndexOf(':');
        if (separator <= 0)
        {
            return maybeScoped;
        }

        if (!TryParseRegion(maybeScoped[..separator], out _))
        {
            return maybeScoped;
        }

        return maybeScoped[(separator + 1)..];
    }

    /// <summary>
    /// Case-insensitive region-tag lookup that never throws. Delegates to
    /// <see cref="EnumWire{TEnum}.TryParse"/> so the wire vocabulary flows from the
    /// <see cref="System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute"/>s
    /// on <see cref="ScyllaKeyspace"/> — a single source of truth shared with the
    /// JSON boundary. Differs from <see cref="EnumWireExtensions.ParseScyllaKeyspace"/>
    /// only in dropping the "blank defaults to Nam" fallback: a blank prefix here is
    /// unambiguously "no prefix at all", not a signal to fall back.
    /// </summary>
    internal static bool TryParseRegion(string raw, out ScyllaKeyspace region)
        => raw.TryParseWire(out region);
}

/// <summary>
/// Emits and reads the raw <see cref="ScopedSystemId.Value"/> string — byte-identical to
/// <c>SystemIdJsonConverter</c> so migrating a field's declared type from
/// <see cref="SystemId"/> to <see cref="ScopedSystemId"/> produces zero wire change.
/// </summary>
internal sealed class ScopedSystemIdJsonConverter : JsonConverter<ScopedSystemId>
{
    public override ScopedSystemId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        // The strict parse is deliberate here: inbound JSON that lost the scope during
        // hand-crafting a fixture or during a bad publisher would otherwise silently
        // deserialise to an invalid ScopedSystemId. Fail-fast on the first bad byte.
        return ScopedSystemId.ParseScoped(raw ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, ScopedSystemId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
