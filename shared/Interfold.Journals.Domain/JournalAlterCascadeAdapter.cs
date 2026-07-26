using Interfold.Journals.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Journals.Domain;

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
