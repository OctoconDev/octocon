using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Abstractions;

/// <summary>Cascade hook for alter deletion — removes every alter-journal entry owned by the
/// alter and detaches the alter from any global-journal rows (the global journal itself is
/// left in place; multiple alters can share it). Adapter over <c>IJournalRepository</c> in
/// <c>Interfold.Journals.Domain</c>, registered by <c>AddJournalsModule</c>.</summary>
public interface IJournalAlterCascade
{
    Task<int> DeleteAllForAlterAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default);
}
