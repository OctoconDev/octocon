using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;

namespace Interfold.Settings.Domain.Abstractions.Repository;

public interface IEncryptionStateRepository
{
    Task<EncryptionState?> GetAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<bool> UpsertAsync(SystemId systemId, bool initialized, KeyChecksum? keyChecksum, EncryptionSalt? salt, CancellationToken cancellationToken = default);
}
