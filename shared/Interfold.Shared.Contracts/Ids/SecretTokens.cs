namespace Interfold.Shared.Contracts.Ids;

// Wrapper ToString() calls this so accidental interpolation cannot leak the raw value;
// use .Value at serialization / DB / HTTP boundaries.
public static class SecretRedaction
{
    public static string Redact(string value)
        => value.Length <= 4 ? "…" : $"{value[..4]}…";
}
