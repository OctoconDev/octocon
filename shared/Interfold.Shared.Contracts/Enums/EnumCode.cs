namespace Interfold.Shared.Contracts.Enums;

/// <summary>Persistence-code (<see cref="short"/>) round-trip for enums backing a smallint
/// column. Outbound is a plain <c>(short)value</c> cast at the call site (every consumer
/// uses a <c>: short</c> backing). Inbound: throwing FromCode, defaulting FromCode(fallback),
/// or the bool + out TryFromCode. Prefer the extension-method form in EnumCodeExtensions.</summary>
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

    /// <summary>Convert <paramref name="code"/> to <typeparamref name="TEnum"/>; null/unknown
    /// returns <paramref name="fallback"/> when supplied, otherwise throws.</summary>
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

    /// <summary>Convert <paramref name="code"/> to <typeparamref name="TEnum"/>?; null/unknown → null.</summary>
    public static TEnum? FromCodeOrNull(short? code)
    {
        if (code is { } c && ByCode.TryGetValue(c, out var value))
        {
            return value;
        }

        return null;
    }

    /// <summary>Bool + out safe lookup; null/unknown → false.</summary>
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
