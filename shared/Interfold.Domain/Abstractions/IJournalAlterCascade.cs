using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Domain.Abstractions;

/// <summary>Cascade hook for alter deletion — removes every alter-journal entry owned by the
/// alter and detaches the alter from any global-journal rows (without deleting the global
/// journal itself; multiple alters can share it). Exposed as a spine-level abstraction so
/// <c>DeleteAlterCommandHandler</c> (Alters.Domain) can trigger the cleanup without a
/// Journals.Domain reference (would form an Alters.Domain &lt;-&gt; Journals.Domain cycle
/// symmetric with <see cref="IAlterExistenceCheck"/>). Implemented by an adapter over
/// <c>IJournalRepository</c> in <c>Interfold.Journals.Domain</c>, registered by
/// <c>AddJournalsModule</c>.</summary>
public interface IJournalAlterCascade
{
    Task<int> DeleteAllForAlterAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default);
}
