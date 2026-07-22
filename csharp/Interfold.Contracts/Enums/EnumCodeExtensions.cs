namespace Interfold.Contracts.Enums;

// Extension-method surface for EnumCode<T>. Reflection tables live on the generic type;
// this file adds ergonomic call syntax so callers write `code.FromCode<VisibilityLevel>()`.
public static class EnumCodeExtensions
{
    public static TEnum FromCode<TEnum>(this short? code, TEnum fallback) where TEnum : struct, Enum
        => EnumCode<TEnum>.FromCode(code, fallback);

    public static TEnum FromCode<TEnum>(this short code, TEnum fallback) where TEnum : struct, Enum
        => EnumCode<TEnum>.FromCode(code, fallback);

    /// <summary>Fallback-less persistence-read form: unknown / null throws. Silently
    /// defaulting a corrupt row masks data corruption; use fallback overloads only for
    /// in-process business-default cases.</summary>
    public static TEnum FromCode<TEnum>(this short? code) where TEnum : struct, Enum
        => EnumCode<TEnum>.FromCode(code);

    public static TEnum FromCode<TEnum>(this short code) where TEnum : struct, Enum
        => EnumCode<TEnum>.FromCode(code);

    public static TEnum? FromCodeOrNull<TEnum>(this short? code) where TEnum : struct, Enum
        => EnumCode<TEnum>.FromCodeOrNull(code);

    public static TEnum? FromCodeOrNull<TEnum>(this short code) where TEnum : struct, Enum
        => EnumCode<TEnum>.FromCodeOrNull(code);

    public static bool TryFromCode<TEnum>(this short? code, out TEnum value) where TEnum : struct, Enum
        => EnumCode<TEnum>.TryFromCode(code, out value);
}
