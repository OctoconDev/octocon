namespace Interfold.Contracts.Enums;

/// <summary>
/// Extension-method surface for <see cref="EnumCode{TEnum}"/>. Mirrors the wire-side
/// pairing of <c>EnumWire&lt;T&gt;</c> / <c>EnumWireExtensions</c>: the generic type holds
/// the reflection-built lookup tables, this class holds the ergonomic call syntax so
/// callers write <c>code.FromCode&lt;VisibilityLevel&gt;()</c> instead of
/// <c>EnumCode&lt;VisibilityLevel&gt;.FromCode(code)</c>.
/// </summary>
public static class EnumCodeExtensions
{
    /// <summary>
    /// Extension-method form of <see cref="EnumCode{TEnum}.FromCode"/> with a required
    /// fallback — <typeparamref name="TEnum"/> is inferred from <paramref name="fallback"/>'s
    /// type. Prefer the fallback-less overload for values sourced from persistence; the
    /// fallback shape stays for the rare in-process case where a business default is the
    /// right answer for an unknown code.
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
    /// Fallback-less form for on-disk enum codes: unknown or null values throw
    /// <see cref="ArgumentOutOfRangeException"/>. Use this at every persistence read site
    /// — silently defaulting a corrupt row to some "safe" enum member (as the fallback
    /// overloads do) masks data corruption, and rows populated by the API always write a
    /// declared member. <typeparamref name="TEnum"/> can't be inferred without the fallback,
    /// so callers must supply it explicitly (<c>code.FromCode&lt;VisibilityLevel&gt;()</c>).
    /// </summary>
    public static TEnum FromCode<TEnum>(this short? code) where TEnum : struct, Enum
        => EnumCode<TEnum>.FromCode(code);

    /// <summary>
    /// Non-nullable-receiver counterpart to the fallback-less <see cref="FromCode{TEnum}(short?)"/>.
    /// Same "throw on unknown" contract; separate overload so <c>row.GetValue&lt;short&gt;(...)</c>
    /// call sites don't have to lift into <see cref="Nullable{T}"/>.
    /// </summary>
    public static TEnum FromCode<TEnum>(this short code) where TEnum : struct, Enum
        => EnumCode<TEnum>.FromCode(code);

    /// <summary>
    /// Extension-method form of <see cref="EnumCode{TEnum}.FromCodeOrNull"/>. Callers must
    /// supply <typeparamref name="TEnum"/> explicitly (<c>code.FromCodeOrNull&lt;VisibilityLevel&gt;()</c>).
    /// </summary>
    public static TEnum? FromCodeOrNull<TEnum>(this short? code) where TEnum : struct, Enum
        => EnumCode<TEnum>.FromCodeOrNull(code);

    /// <summary>
    /// Non-nullable-receiver counterpart to <see cref="FromCodeOrNull{TEnum}(short?)"/>.
    /// </summary>
    public static TEnum? FromCodeOrNull<TEnum>(this short code) where TEnum : struct, Enum
        => EnumCode<TEnum>.FromCodeOrNull(code);

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
