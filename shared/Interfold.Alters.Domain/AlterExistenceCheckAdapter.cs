using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Alters;

// see IAlterExistenceCheck for the cycle rationale
public sealed class AlterExistenceCheckAdapter : IAlterExistenceCheck
{
    private readonly IAlterRepository _alterRepository;

    public AlterExistenceCheckAdapter(IAlterRepository alterRepository)
    {
        _alterRepository = alterRepository;
    }

    public Task<bool> ExistsAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
        => _alterRepository.ExistsAsync(systemId, alterId, cancellationToken);
}
