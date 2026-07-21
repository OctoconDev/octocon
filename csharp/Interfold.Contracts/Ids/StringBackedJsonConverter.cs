using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// Common JSON converter shape for every string-backed wrapper struct across the codebase —
/// the ~11 plain public string wrappers (<c>SystemId</c>, <c>DiscordId</c>, <c>Email</c>,
/// <c>AppleId</c>, <c>AvatarUrl</c>, <c>EncryptionKeyMaterial</c>, <c>EntityRef</c>,
/// <c>IdempotencyKey</c>, <c>OperationId</c>, <c>ErrorCode</c>, <c>PollChoiceId</c>) AND
/// the six PII/secret tokens (<c>LinkToken</c>, <c>PushToken</c>, <c>ImportToken</c>,
/// <c>RecoveryCode</c>, <c>Jti</c>, <c>SocketToken</c>). Both families share the same
/// wire body (raw string in, raw string out) — the redaction difference lives on the
/// struct's own <c>ToString()</c> override, not on the converter, so a single base
/// covers both. The remaining family-scoped base (<see cref="GuidIdJsonConverter{T}"/>
/// for the five Guid entity IDs) stays separate because its wire body is a Guid, not a
/// string.
///
/// <para>
/// Read is null-tolerant (<c>null</c> becomes <c>string.Empty</c>) rather than throwing,
/// matching the behaviour of the hand-rolled bodies this base replaces. Individual
/// subclasses that need different behaviour (e.g. <c>UsernameJsonConverter</c>, which
/// sets <c>HandleNull = true</c> and inspects <c>TokenType == Null</c>) stay hand-rolled
/// from <see cref="JsonConverter{T}"/> directly — the same pattern
/// <see cref="GuidIdJsonConverter{T}"/> uses (<c>sealed override</c> its
/// <c>Read</c>/<c>Write</c>).
/// </para>
///
/// <para>
/// Concrete stubs stay <c>sealed</c> and are named individually because
/// <c>[JsonConverter(typeof(...))]</c> attribute application requires a concrete class name.
/// </para>
/// </summary>
internal abstract class StringBackedJsonConverter<T> : JsonConverter<T>
{
    protected abstract T Create(string value);
    protected abstract string GetValue(T value);

    public sealed override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Create(reader.GetString() ?? string.Empty);

    public sealed override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(GetValue(value));
}
