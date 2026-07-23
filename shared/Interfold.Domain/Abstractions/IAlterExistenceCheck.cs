using Interfold.Contracts;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Abstractions;

/// <summary>Narrow existence-check for an alter row, exposed as a spine-level abstraction
/// so cross-feature handlers (Journals, Tags) can validate an <see cref="AlterId"/> without
/// back-referencing <c>Interfold.Alters.Domain</c>. The natural <see cref="Interfold.Domain.Abstractions.Repository.ISettingsFieldRepository"/>-shaped
/// approach — inject <c>IAlterRepository</c> directly — would form an Alters.Domain &lt;-&gt;
/// Journals.Domain cycle (Alters.Domain injects <c>IJournalAlterCascade</c> for delete-cascade,
/// symmetric direction). Implemented by an adapter over <c>IAlterRepository</c> in
/// <c>Interfold.Alters.Domain</c>, registered by <c>AddAltersModule</c>.</summary>
public interface IAlterExistenceCheck
{
    Task<bool> ExistsAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default);
}
