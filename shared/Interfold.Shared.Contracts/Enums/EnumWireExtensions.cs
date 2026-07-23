namespace Interfold.Shared.Contracts.Enums;

// Parse-side helpers for env-driven enums: blank → compiled-in default, unknown → throw
// (so an operator typo can't silently degrade a Primary to an Auxiliary).
// Tolerant "unknown → null" callers invoke EnumWire<T>.TryParse directly.
public static class EnumWireExtensions
{
    public static string ToWire<TEnum>(this TEnum value) where TEnum : struct, Enum
        => EnumWire<TEnum>.ToWire(value);

    public static bool TryParseWire<TEnum>(this string? raw, out TEnum value) where TEnum : struct, Enum
        => EnumWire<TEnum>.TryParse(raw, out value);

    // Blank → default, unknown-non-empty → throw. Shared shape for env-parsers below.
    public static T ParseWithDefault<T>(string? raw, T defaultValue, Func<string, string> errorProvider) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (raw.TryParseWire<T>(out var value))
        {
            return value;
        }

        throw new InvalidOperationException(errorProvider(raw.Trim()));
    }

    public static NodeGroup ParseNodeGroup(string? raw)
        => ParseWithDefault(raw, NodeGroup.Auxiliary, trimmed => $"Unrecognised node group '{trimmed}'. Valid values: primary, auxiliary, sidecar.");

    public static ScyllaKeyspace ParseScyllaKeyspace(string? raw)
        => ParseWithDefault(raw, ScyllaKeyspace.Nam, trimmed => $"Unrecognised scylla keyspace '{trimmed}'. Valid values: nam, eur, sam, sas, eas, ocn, gdpr.");

    public static PersistenceMode ParsePersistenceMode(string? raw)
        => ParseWithDefault(raw, PersistenceMode.ScyllaPostgres, trimmed => $"Unsupported OCTOCON_PERSISTENCE value '{trimmed}'. Expected: scylla-postgres | inmemory.");
}
