using System.Collections.Immutable;
using Interfold.Contracts.Secrets;

namespace Interfold.Api.Services.Secrets;

/// <summary>
/// Thread-safe in-memory <see cref="ISecretsSnapshot"/>. Written to exactly once by
/// <see cref="SecretsPreBuildLoader"/> before the host builds; every subsequent read (from the
/// two <see cref="Microsoft.Extensions.Options.IPostConfigureOptions{TOptions}"/> patchers)
/// is a lock-free dictionary lookup.
///
/// <para>
/// The backing store is <see cref="ImmutableDictionary{TKey, TValue}"/> so publish of the
/// populated map is a single reference assignment with a memory barrier. Consumers that
/// read <see cref="_values"/> after <see cref="IsPopulated"/> flips see the complete map,
/// which is the ordering the loader relies on to avoid a torn read.
/// </para>
/// </summary>
internal sealed class SecretsSnapshot : ISecretsSnapshot
{
    private ImmutableDictionary<string, string> _values = ImmutableDictionary<string, string>.Empty;

    public bool IsPopulated { get; private set; }

    public string? Get(SecretsStoreKey key)
        => _values.TryGetValue(key.Value, out var value) ? value : null;

    /// <summary>
    /// Called by <see cref="SecretsPreBuildLoader.Load"/> exactly once with the full set of
    /// rows read from Postgres (or the <c>OCTOCON_INMEMORY_SECRETS_SEED:*</c> config keys in
    /// the no-Postgres branch). Blank / null values are filtered out so <see cref="Get"/>
    /// returns <c>null</c> for a row that exists in the store but was seeded with an empty
    /// string — the post-configure patchers treat "row absent" and "row empty" as the same
    /// state so a partially-seeded deployment behaves like an unseeded one instead of trying
    /// to use an empty credential.
    /// </summary>
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
