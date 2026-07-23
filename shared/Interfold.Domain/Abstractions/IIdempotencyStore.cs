using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;

namespace Interfold.Domain.Abstractions;

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