using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;

namespace Interfold.Shared.Domain.Abstractions;

public interface IIdempotencyStore
{
    Task<IdempotencyMatch?> FindAsync(
        SystemId principalId,
        OperationId operationId,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default
    );

    Task SaveAsync(
        SystemId principalId,
        OperationId operationId,
        IdempotencyKey idempotencyKey,
        string payloadHash,
        string outcomeHash,
        string? outcomePayload,
        CancellationToken cancellationToken = default
    );
}