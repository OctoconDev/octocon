using System.Collections.Concurrent;
using Interfold.Shared.Contracts.Secrets;

namespace Interfold.Infrastructure.InMemory;

public sealed class InMemorySecretsStore : ISecretsStore
{
    private readonly ConcurrentDictionary<string, SecretEntry> _secrets = new();

    public Task<string?> GetAsync(SecretsStoreKey key, CancellationToken cancellationToken = default)
    {
        _secrets.TryGetValue(key.Value, out var entry);
        return Task.FromResult(entry?.Value);
    }

    public Task<string> GetRequiredAsync(SecretsStoreKey key, CancellationToken cancellationToken = default)
    {
        if (!_secrets.TryGetValue(key.Value, out var entry))
            throw new InvalidOperationException($"Required secret '{key.Value}' not found in secrets store.");
        return Task.FromResult(entry.Value);
    }

    public Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SecretEntry> result = _secrets.Values
            .OrderBy(e => e.Key.Value)
            .ToList();
        return Task.FromResult(result);
    }

    /// <summary>
    /// Internal helper for test seeding. Not part of the ISecretsStore contract.
    /// </summary>
    public void Seed(SecretsStoreKey key, string value)
    {
        var now = DateTimeOffset.UtcNow;
        _secrets.AddOrUpdate(key.Value,
            _ => new SecretEntry(key, value, "in-memory", now, now, null, null),
            (_, existing) => existing with { Value = value, UpdatedAt = now });
    }
}
