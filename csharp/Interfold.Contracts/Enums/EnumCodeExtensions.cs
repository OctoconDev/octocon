namespace Interfold.Contracts.Enums;

/// <summary>
/// Extension-method surface for <see cref="EnumCode{TEnum}"/>. Mirrors the wire-side
/// pairing of <c>EnumWire&lt;T&gt;</c> / <c>EnumWireExtensions</c>: the generic type holds
/// the reflection-built lookup tables, this class holds the ergonomic call syntax so
/// callers write <c>code.FromCode(VisibilityLevel.Public)</c> instead of
/// <c>EnumCode&lt;VisibilityLevel&gt;.FromCode(code, VisibilityLevel.Public)</c>.
/// </summary>
public static class EnumCodeExtensions
{
    /// <summary>
    /// Extension-method form of <see cref="EnumCode{TEnum}.FromCode"/> with a required
    /// fallback — <typeparamref name="TEnum"/> is inferred from <paramref name="fallback"/>'s
    /// type, so callers read as <c>code.FromCode(VisibilityLevel.Public)</c>. The throwing
    /// no-fallback shape stays reachable via <see cref="EnumCode{TEnum}.FromCode"/> directly
    /// (no caller in the codebase uses it today, but the affordance stays).
    /// </summary>
    public static TEnum FromCode<TEnum>(this short? code, TEnum fallback) where TEnum : struct, Enum
        => EnumCode<TEnum>.FromCode(code, fallback);

    /// <summary>
    /// Overload accepting a non-nullable <see cref="short"/> receiver so callers with
    /// <c>row.GetValue&lt;short&gt;(...)</c> don't have to lift the value into
    /// <see cref="Nullable{T}"/> before invoking the extension. Delegates to the same
    /// <see cref="EnumCode{TEnum}.FromCode"/> path.
    /// </summary>
    public static TEnum FromCode<TEnum>(this short code, TEnum fallback) where TEnum : struct, Enum
        => EnumCode<TEnum>.FromCode(code, fallback);

    /// <summary>
    /// Extension-method form of <see cref="EnumCode{TEnum}.TryFromCode"/>. Callers using
    /// <c>out var</c> need to supply <typeparamref name="TEnum"/> explicitly
    /// (<c>code.TryFromCode&lt;AvatarSource&gt;(out var src)</c>) because
    /// <see langword="out"/> targets typed as <c>var</c> don't participate in generic
    /// type inference.
    /// </summary>
    public static bool TryFromCode<TEnum>(this short? code, out TEnum value) where TEnum : struct, Enum
        => EnumCode<TEnum>.TryFromCode(code, out value);
}
