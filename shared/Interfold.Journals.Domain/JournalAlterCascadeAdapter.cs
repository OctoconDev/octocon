using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;

namespace Interfold.Shared.Domain.Journals;

// see IJournalAlterCascade for the cycle rationale
public sealed class JournalAlterCascadeAdapter : IJournalAlterCascade
{
    private readonly IJournalRepository _journalRepository;

    public JournalAlterCascadeAdapter(IJournalRepository journalRepository)
    {
        _journalRepository = journalRepository;
    }

    public Task<int> DeleteAllForAlterAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
        => _journalRepository.DeleteAllForAlterAsync(systemId, alterId, cancellationToken);
}
