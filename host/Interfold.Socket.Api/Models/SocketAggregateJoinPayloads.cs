using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;

namespace Interfold.Contracts;

// Socket aggregate join payloads. Namespace preserved as Interfold.Contracts for
// wire-compat; physical file moved out of shared/Interfold.Contracts/SocketPayloadContracts.cs
// during the Phase-3 Alters slice to break the spine-batch-DTO cycle with
// Interfold.Alters.Contracts (each binds IReadOnlyList<AlterReadModel>). These migrate
// into the Socket module (Phase 3 #10); the same relocation is what lets AlterReadModel,
// TagReadModel, and FrontActiveReadModel move off the spine into their per-feature Contracts.

public sealed record SocketJoinInitPayload(
    SocketSelfReadModel System,
    IReadOnlyList<AlterReadModel> Alters,
    IReadOnlyList<FrontActiveReadModel> Fronts,
    IReadOnlyList<TagReadModel> Tags) : ISocketPayload;

public sealed record SocketJoinBatchedPayload(
    bool Batched,
    SocketSelfReadModel System,
    IReadOnlyList<AlterReadModel>? Alters,
    IReadOnlyList<FrontActiveReadModel>? Fronts,
    IReadOnlyList<TagReadModel>? Tags) : ISocketPayload;

public sealed record SocketBatchedAltersPayload(int BatchIndex, int TotalBatches, IReadOnlyList<AlterReadModel> Alters) : ISocketPayload;
