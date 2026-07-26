using System.Collections.Concurrent;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryEncryptionStateRepository : IEncryptionStateRepository
{
    private readonly ConcurrentDictionary<SystemId, EncryptionState> _states = new();

    public Task<EncryptionState?> GetAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSystemId = InMemoryStorageKeys.Normalize(systemId);
        _states.TryGetValue(normalizedSystemId, out var state);
        return Task.FromResult(state);
    }

    public Task<bool> UpsertAsync(SystemId systemId, bool initialized, KeyChecksum? keyChecksum, EncryptionSalt? salt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSystemId = InMemoryStorageKeys.Normalize(systemId);

        _states.TryGetValue(normalizedSystemId, out EncryptionState? value);
        _states[normalizedSystemId] = new EncryptionState(Initialized: initialized, KeyChecksum: keyChecksum,
            Salt: salt ?? value?.Salt);

        return Task.FromResult(true);
    }
}
