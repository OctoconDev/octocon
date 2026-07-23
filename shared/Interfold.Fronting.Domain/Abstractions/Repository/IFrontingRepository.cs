using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Read;

namespace Interfold.Domain.Abstractions.Repository;

public interface IFrontingRepository
{
    Task<bool> IsFrontingAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default);

    Task<FrontId?> StartAsync(SystemId systemId, AlterId alterId, string? comment, DateTimeOffset startedAt, CancellationToken cancellationToken = default);

    Task<bool> EndAsync(SystemId systemId, AlterId alterId, DateTimeOffset endedAt, CancellationToken cancellationToken = default);

    Task<bool> SetPrimaryAsync(SystemId systemId, AlterId? alterId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FrontActiveReadModel>> ListActiveAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FrontActiveReadModel>> ListActiveGuardedAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<FrontHistoryReadModel>> ListHistoryBetweenAsync(
        SystemId systemId,
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default);

    Task<FrontActiveReadModel?> GetActiveByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default);

    Task<FrontHistoryReadModel?> GetHistoryEntryByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default);

    Task<bool> EndByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default);

    Task<bool> DeleteFrontByIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default);

    Task<bool> UpdateCommentByFrontIdAsync(SystemId systemId, FrontId frontId, string comment, CancellationToken cancellationToken = default);
}