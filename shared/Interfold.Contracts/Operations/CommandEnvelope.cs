using System.Net;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Operations;

/// <summary>
/// Command envelope carried through every write path. <see cref="PrincipalId"/> is a
/// <see cref="ScopedSystemId"/> so command handlers, event publishers, idempotency stores,
/// and the WebSocket router see a compile-time guarantee that the principal is always in
/// wire-canonical <c>{region}:{rawId}</c> form. The middleware performs the one strict
/// parse of the JWT <c>sub</c> claim on ingress; every downstream hop uses
/// <see cref="ScopedSystemId.AsSystemId"/> at the storage boundary and never re-parses.
/// </summary>
public sealed record CommandEnvelope<TPayload>(
    OperationId OperationId,
    Guid CommandId,
    ScopedSystemId PrincipalId,
    IdempotencyKey IdempotencyKey,
    DateTimeOffset? OccurredAt,
    TPayload Payload,
    HttpStatusCode? SuccessStatusCode = null
);
