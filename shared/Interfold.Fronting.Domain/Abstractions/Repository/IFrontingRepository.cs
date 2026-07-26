using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;

namespace Interfold.Shared.Domain.Abstractions.Repository;

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

    /// <summary>Every front on record for <paramref name="systemId"/>, open + closed, ordered
    /// by <c>time_start</c> descending. Rows with a null <c>time_start</c> are dropped —
    /// export path only surfaces populated rows (mirrors accounts.ex:1215's filter). Bypasses
    /// the <c>fronts_by_time</c> denormalisation because it's only populated on close.</summary>
    Task<IReadOnlyList<FrontHistoryReadModel>> ListAllAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<FrontActiveReadModel?> GetActiveByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default);

    Task<FrontHistoryReadModel?> GetHistoryEntryByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default);

    Task<bool> EndByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default);

    Task<bool> DeleteFrontByIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default);

    Task<bool> UpdateCommentByFrontIdAsync(SystemId systemId, FrontId frontId, string comment, CancellationToken cancellationToken = default);
}