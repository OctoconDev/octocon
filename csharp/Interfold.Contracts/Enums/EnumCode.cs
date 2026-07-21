namespace Interfold.Contracts.Enums;

/// <summary>
/// Persistence-code (<see cref="short"/>) round-trip for enums that back a <c>smallint</c>
/// database column. The outbound direction is a plain <c>(short)value</c> cast at the call
/// site — every enum this helper is used with is declared with a <c>: short</c> backing type,
/// so the cast is the operation and no wrapper method is provided.
/// </summary>
/// <remarks>
/// Inbound conversion is expressed as either a throwing <see cref="FromCode"/> (no fallback)
/// or a defaulting <see cref="FromCode"/> (fallback supplied). The optional-parameter shape
/// supplants the per-enum <c>FromCode</c> / <c>FromCodeOrPublic</c> / <c>FromCodeOrPrivate</c>
/// wrappers that historically encoded the fallback in the method name; callers now name the
/// safety default at the call site. <see cref="TryFromCode"/> matches the shape of
/// <see cref="EnumWire{TEnum}.TryParse"/> for callers that want the bool + out pattern.
///
/// <para>
/// Call sites should prefer the extension-method form defined in
/// <see cref="EnumCodeExtensions"/>: <c>code.FromCode(VisibilityLevel.Public)</c> and
/// <c>code.TryFromCode&lt;AvatarSource&gt;(out var src)</c>. Reach for the static form
/// on this type only when the extension can't be used (e.g. no fallback and the throwing
/// path is intentional).
/// </para>
/// </remarks>
public static class EnumCode<TEnum> where TEnum : struct, Enum
{
    private static readonly Dictionary<short, TEnum> ByCode;

    static EnumCode()
    {
        var values = Enum.GetValues<TEnum>();
        ByCode = new Dictionary<short, TEnum>(values.Length);
        foreach (var v in values)
        {
            ByCode[Convert.ToInt16(v)] = v;
        }
    }

    /// <summary>
    /// Convert <paramref name="code"/> back to <typeparamref name="TEnum"/>. When
    /// <paramref name="code"/> is <see langword="null"/> or not a declared member, returns
    /// <paramref name="fallback"/> if supplied, otherwise throws
    /// <see cref="ArgumentOutOfRangeException"/>. The optional fallback replaces the per-enum
    /// <c>FromCode</c> / <c>FromCodeOrPublic</c> / <c>FromCodeOrPrivate</c> wrappers.
    /// </summary>
    public static TEnum FromCode(short? code, TEnum? fallback = null)
    {
        if (code is { } c && ByCode.TryGetValue(c, out var value))
        {
            return value;
        }

        if (fallback is { } fb)
        {
            return fb;
        }

        throw new ArgumentOutOfRangeException(nameof(code), code,
            $"Unhandled {typeof(TEnum).Name} code.");
    }

    /// <summary>
    /// Convert <paramref name="code"/> back to <typeparamref name="TEnum"/>, returning
    /// <see langword="null"/> if the code is unknown or <see langword="null"/>.
    /// </summary>
    public static TEnum? FromCodeOrNull(short? code)
    {
        if (code is { } c && ByCode.TryGetValue(c, out var value))
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// Bool + out safe lookup. Returns <see langword="false"/> for <see langword="null"/> or
    /// unknown codes; matches the shape of <see cref="EnumWire{TEnum}.TryParse"/>.
    /// </summary>
    public static bool TryFromCode(short? code, out TEnum value)
    {
        if (code is { } c && ByCode.TryGetValue(c, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}
