using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;

namespace Interfold.Shared.Contracts;

// Extracted from Interfold.Shared.Contracts/SocketPayloadContracts.cs during Phase-3 Tags migration.
// The cross-feature aggregate join payloads (SocketJoinInitPayload, SocketJoinBatchedPayload)
// still reference IReadOnlyList<TagReadModel> and stay in Interfold.Shared.Contracts — Interfold.Shared.Contracts
// grew a spine back-ref to Interfold.Tags.Contracts to keep them compiling. Both dissolve when
// the Socket feature (Phase 3 #10) takes the aggregate join payloads.

public sealed record SocketBatchedTagsPayload(int BatchIndex, int TotalBatches, IReadOnlyList<TagReadModel> Tags) : ISocketPayload;

public sealed record TagSocketPayload(TagReadModel Tag) : ISocketPayload;

public sealed record TagDeletedSocketPayload(TagId TagId) : ISocketPayload;
