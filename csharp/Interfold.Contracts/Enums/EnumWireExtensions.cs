using Interfold.Contracts.Models.ImportOperations;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Parse-side helpers that layer a fallback-then-throw shape on top of
/// <see cref="EnumWire{TEnum}.TryParse"/> — used by the env-driven enums whose semantic
/// differs between "operator left it blank" (fall back to a compiled-in default) and
/// "operator typed something we don't recognise" (throw so a typo can't silently degrade a
/// Primary to an Auxiliary, a nam keyspace to eur, or ScyllaPostgres to InMemory).
/// Tolerant "unknown → null" callers invoke <c>EnumWire&lt;T&gt;.TryParse</c> directly.
///
/// <para>
/// The write-side counterpart is the <see cref="ToWire{TEnum}(TEnum)"/> extension below —
/// the <see cref="System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute"/> stays the
/// single source of truth for each enum's wire vocabulary.
/// </para>
/// </summary>
public static class EnumWireExtensions
{
    /// <summary>Extension-method form of <see cref="EnumWire{TEnum}.ToWire"/>.</summary>
    public static string ToWire<TEnum>(this TEnum value) where TEnum : struct, Enum
        => EnumWire<TEnum>.ToWire(value);

    /// <summary>
    /// Extension-method form of <see cref="EnumWire{TEnum}.TryParse"/>. <typeparamref name="TEnum"/>
    /// is unambiguous from the <c>out</c> target's declared type at every call site (route-value
    /// parses, JSON reader strings, env values), so callers read as
    /// <c>raw.TryParseWire&lt;OAuthProvider&gt;(out var provider)</c> — mirroring the
    /// <c>int.TryParse</c> / <c>Guid.TryParse</c> shape .NET developers already reach for.
    /// </summary>
    public static bool TryParseWire<TEnum>(this string? raw, out TEnum value) where TEnum : struct, Enum
        => EnumWire<TEnum>.TryParse(raw, out value);

    public static ImportOperationKind ParseWireValue(string value)
    {
        if (value.TryParseWire<ImportOperationKind>(out var kind))
        {
            return kind;
        }

        throw new ArgumentException(
            $"'{value}' is not a valid ImportOperationKind wire value. Expected 'sp' or 'pk'.",
            nameof(value));
    }

    /// <summary>
    /// Parses a raw <c>OCTOCON_NODE_GROUP</c> / <c>FLY_PROCESS_GROUP</c> env value into a
    /// <see cref="NodeGroup"/>. Null / empty / whitespace resolves to <see cref="NodeGroup.Auxiliary"/>
    /// (the historical default); unknown non-empty values throw so an operator typo doesn't silently
    /// degrade a Primary to an Auxiliary.
    /// </summary>
    public static NodeGroup ParseNodeGroup(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return NodeGroup.Auxiliary;
        }

        if (raw.TryParseWire<NodeGroup>(out var group))
        {
            return group;
        }

        throw new InvalidOperationException(
            $"Unrecognised node group '{raw.Trim()}'. Valid values: primary, auxiliary, sidecar.");
    }

    /// <summary>
    /// Parses a raw <c>OCTOCON_SCYLLA_KEYSPACE</c> / <c>databaseMode</c>-preview string into a
    /// <see cref="ScyllaKeyspace"/>. Null / empty resolves to <see cref="ScyllaKeyspace.Nam"/> (the
    /// deployment-time default). Unknown non-empty values throw for the same reason as
    /// <see cref="ParseNodeGroup"/>.
    /// </summary>
    public static ScyllaKeyspace ParseScyllaKeyspace(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ScyllaKeyspace.Nam;
        }

        if (raw.TryParseWire<ScyllaKeyspace>(out var keyspace))
        {
            return keyspace;
        }

        throw new InvalidOperationException(
            $"Unrecognised scylla keyspace '{raw.Trim()}'. Valid values: nam, eur, sam, sas, eas, ocn, gdpr.");
    }

    /// <summary>
    /// Parses a raw <c>OCTOCON_PERSISTENCE</c> env value into a <see cref="PersistenceMode"/>.
    /// Null / empty / whitespace resolves to <see cref="PersistenceMode.ScyllaPostgres"/> (the
    /// historical default); unknown non-empty values throw so an operator typo lands at boot
    /// rather than as a silent switch to the wrong backend.
    /// </summary>
    public static PersistenceMode ParsePersistenceMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return PersistenceMode.ScyllaPostgres;
        }

        if (raw.TryParseWire<PersistenceMode>(out var mode))
        {
            return mode;
        }

        throw new InvalidOperationException(
            $"Unsupported OCTOCON_PERSISTENCE value '{raw.Trim()}'. Expected: scylla-postgres | inmemory.");
    }
}
