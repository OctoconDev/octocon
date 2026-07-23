using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;

namespace Interfold.Domain.Abstractions.Repository;

public interface IAlterRepository
{
    Task<AlterId?> CreateAsync(SystemId systemId, CreateAlterCommand command, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(SystemId systemId, UpdateAlterCommand command, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlterReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BareAlter>> ListGuardedAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default
    );

    Task<AlterReadModel?> GetAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default);

    Task<BareAlter?> GetGuardedAsync(
        SystemId systemId,
        AlterId alterId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default
    );

    Task<bool> AliasTakenByOtherAsync(
        SystemId systemId,
        AlterId alterId,
        string alias,
        CancellationToken cancellationToken = default
    );
}