using System.Collections.Immutable;
using Interfold.Shared.Contracts.Secrets;

namespace Interfold.Api.Services.Secrets;

/// <summary>Thread-safe in-memory <see cref="ISecretsSnapshot"/>. Written exactly once by
/// <see cref="SecretsPreBuildLoader"/> pre-Build; reads are lock-free dictionary lookups.
/// Immutable backing so the populated map publishes via a single reference assignment.</summary>
internal sealed class SecretsSnapshot : ISecretsSnapshot
{
    private ImmutableDictionary<string, string> _values = ImmutableDictionary<string, string>.Empty;

    public bool IsPopulated { get; private set; }

    public string? Get(SecretsStoreKey key)
        => _values.TryGetValue(key.Value, out var value) ? value : null;

    /// <summary>Blank/null values are filtered so patchers see "row absent" and "row empty"
    /// identically — partially-seeded deployments behave like unseeded ones.</summary>
    internal void Populate(IReadOnlyDictionary<SecretsStoreKey, string?> values)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                builder[key.Value] = value;
            }
        }
        _values = builder.ToImmutable();
        IsPopulated = true;
    }
}
