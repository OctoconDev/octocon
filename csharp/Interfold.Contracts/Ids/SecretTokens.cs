using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// Shared redaction helper for the secret/token wrapper structs below. <c>ToString()</c> on
/// these types intentionally does NOT return the raw value — accidental logging or string
/// interpolation yields a redacted form (first 4 chars + "…"). Use <c>Value</c> explicitly
/// at serialization/DB/HTTP boundaries.
///
/// <para>
/// The JSON converters for these structs use <see cref="StringBackedJsonConverter{T}"/>
/// (same as the non-secret wrappers) — the wire body IS the raw string in both families,
/// so a single shared converter shape is enough; the "secret" behaviour lives on each
/// struct's own <c>ToString()</c> override below.
/// </para>
/// </summary>
internal static class SecretRedaction
{
    public static string Redact(string value)
        => value.Length <= 4 ? "…" : $"{value[..4]}…";
}

/// <summary>
/// One-time account-link token minted by <c>GET /settings/link_token</c> and consumed by the
/// <c>/auth/link/{provider}</c> flow. JSON serializes as the raw string
/// (<c>LinkTokenReadModel.Token</c> response body is unchanged); <c>ToString()</c> redacts.
/// </summary>
[JsonConverter(typeof(LinkTokenJsonConverter))]
public readonly record struct LinkToken
{
    public string Value { get; }

    public LinkToken(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Explicit narrow so call sites can write <c>(LinkToken)raw</c> instead of
    /// <c>new LinkToken(raw)</c>. No implicit widen by design — see <see cref="DiscordId"/>'s
    /// operator xml-doc for the redaction-preservation rationale. Prefer
    /// <see cref="From(string?)"/> when the input may be null/blank.
    /// </summary>
    public static explicit operator LinkToken(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);

    /// <summary>
    /// Wrap a nullable raw string, returning <c>null</c> for null / empty / whitespace so the
    /// caller's null check runs on the typed <see cref="LinkToken"/>? rather than on a bare
    /// <c>string?</c> local. Keeps the raw token from lingering as a local across a guard/
    /// wrap boundary where an incidental log statement could leak it. Total function — no
    /// throw path.
    /// </summary>
    public static LinkToken? From(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : new LinkToken(value);
}

internal sealed class LinkTokenJsonConverter : StringBackedJsonConverter<LinkToken>
{
    protected override LinkToken Create(string value) => new(value);
    protected override string GetValue(LinkToken value) => value.Value;
}

/// <summary>
/// Device push-notification registration token (FCM/APNs). Carried on
/// <c>AddPushTokenCommand</c>/<c>RemovePushTokenCommand</c> whose payloads are persisted and
/// hashed by the idempotency store — the converter emits the raw string so stored hashes
/// remain valid. <c>ToString()</c> redacts.
/// </summary>
[JsonConverter(typeof(PushTokenJsonConverter))]
public readonly record struct PushToken
{
    public string Value { get; }

    public PushToken(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Explicit narrow so call sites can write <c>(PushToken)raw</c> instead of
    /// <c>new PushToken(raw)</c>. No implicit widen by design — see <see cref="DiscordId"/>'s
    /// operator xml-doc for the redaction-preservation rationale.
    /// </summary>
    public static explicit operator PushToken(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);
}

internal sealed class PushTokenJsonConverter : StringBackedJsonConverter<PushToken>
{
    protected override PushToken Create(string value) => new(value);
    protected override string GetValue(PushToken value) => value.Value;
}

/// <summary>
/// Caller-supplied third-party API token (SimplyPlural or PluralKit) used by the async import
/// pipeline. Carried on <c>ImportSpCommand</c>/<c>ImportPkCommand</c> whose persisted payload
/// hashes must not move — the converter emits the raw string. <c>ToString()</c> redacts.
/// </summary>
[JsonConverter(typeof(ImportTokenJsonConverter))]
public readonly record struct ImportToken
{
    public string Value { get; }

    public ImportToken(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Explicit narrow so call sites can write <c>(ImportToken)raw</c> instead of
    /// <c>new ImportToken(raw)</c>. No implicit widen by design — see <see cref="DiscordId"/>'s
    /// operator xml-doc for the redaction-preservation rationale.
    /// </summary>
    public static explicit operator ImportToken(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);
}

internal sealed class ImportTokenJsonConverter : StringBackedJsonConverter<ImportToken>
{
    protected override ImportToken Create(string value) => new(value);
    protected override string GetValue(ImportToken value) => value.Value;
}

/// <summary>
/// Plaintext encryption recovery code (post-decryption). Carried on <c>ImportSpCommand</c>
/// (persisted payload — raw-string converter keeps hashes valid) and the import job queue.
/// <c>ToString()</c> redacts.
/// </summary>
[JsonConverter(typeof(RecoveryCodeJsonConverter))]
public readonly record struct RecoveryCode
{
    public string Value { get; }

    public RecoveryCode(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Explicit narrow so call sites can write <c>(RecoveryCode)raw</c> instead of
    /// <c>new RecoveryCode(raw)</c>. No implicit widen by design — see <see cref="DiscordId"/>'s
    /// operator xml-doc for the redaction-preservation rationale.
    /// </summary>
    public static explicit operator RecoveryCode(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);
}

internal sealed class RecoveryCodeJsonConverter : StringBackedJsonConverter<RecoveryCode>
{
    protected override RecoveryCode Create(string value) => new(value);
    protected override string GetValue(RecoveryCode value) => value.Value;
}

/// <summary>
/// JWT ID (jti claim) used for token revocation tracking. DB binds unwrap with
/// <see cref="Value"/> (Npgsql cannot bind the struct). <c>ToString()</c> redacts.
/// </summary>
[JsonConverter(typeof(JtiJsonConverter))]
public readonly record struct Jti
{
    public string Value { get; }

    public Jti(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Explicit narrow so call sites can write <c>(Jti)raw</c> instead of <c>new Jti(raw)</c>.
    /// No implicit widen by design — see <see cref="DiscordId"/>'s operator xml-doc for the
    /// redaction-preservation rationale. Prefer <see cref="From(string?)"/> when the input
    /// may be null/blank, and <see cref="NewJti"/> to mint a fresh id in one call.
    /// </summary>
    public static explicit operator Jti(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);

    /// <summary>
    /// Wrap a nullable raw string, returning <c>null</c> for null / empty / whitespace so the
    /// caller's null check runs on the typed <see cref="Jti"/>? rather than on a bare
    /// <c>string?</c> local. Keeps the raw JTI from lingering across a guard/wrap boundary
    /// where an incidental log statement could leak it. Total function — no throw path.
    /// </summary>
    public static Jti? From(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : new Jti(value);

    /// <summary>
    /// Mint a fresh JWT ID as a wrapped <see cref="Jti"/> in one call so JWT-issue sites
    /// don't hold the raw <c>Guid.NewGuid().ToString("N")</c> as a bare local across
    /// downstream sites that would each rewrap. Format is 32-char lowercase hex, no dashes.
    /// Total function — never returns <see langword="default"/>.
    /// </summary>
    public static Jti NewJti() => new(Guid.NewGuid().ToString("N"));
}

internal sealed class JtiJsonConverter : StringBackedJsonConverter<Jti>
{
    protected override Jti Create(string value) => new(value);
    protected override string GetValue(Jti value) => value.Value;
}

/// <summary>
/// The JWT carried in the <c>phx_join</c> payload's <c>token</c> member (mirrors the
/// query-string token the socket upgrade authenticated with). JSON serializes as the raw
/// string; <c>ToString()</c> redacts so a logged join payload can't leak the credential.
/// </summary>
[JsonConverter(typeof(SocketTokenJsonConverter))]
public readonly record struct SocketToken
{
    public string Value { get; }

    public SocketToken(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Explicit narrow so call sites can write <c>(SocketToken)raw</c> instead of
    /// <c>new SocketToken(raw)</c>. No implicit widen by design — see <see cref="DiscordId"/>'s
    /// operator xml-doc for the redaction-preservation rationale.
    /// </summary>
    public static explicit operator SocketToken(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);
}

internal sealed class SocketTokenJsonConverter : StringBackedJsonConverter<SocketToken>
{
    protected override SocketToken Create(string value) => new(value);
    protected override string GetValue(SocketToken value) => value.Value;
}
