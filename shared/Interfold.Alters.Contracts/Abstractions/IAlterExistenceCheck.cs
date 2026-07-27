using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Abstractions;

/// <summary>Existence-check seam so cross-feature handlers (Journals, Tags) can validate an
/// <see cref="AlterId"/> without depending on <c>Interfold.Alters.Domain</c>. Adapter over
/// <c>IAlterRepository</c> in <c>Interfold.Alters.Domain</c>, registered by
/// <c>AddAltersModule</c>.</summary>
public interface IAlterExistenceCheck
{
    Task<bool> ExistsAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default);
}
