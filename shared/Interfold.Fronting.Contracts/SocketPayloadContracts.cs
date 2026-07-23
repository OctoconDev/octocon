using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;

namespace Interfold.Shared.Contracts;

public sealed record SocketBatchedFrontsPayload(int BatchIndex, int TotalBatches, IReadOnlyList<FrontActiveReadModel> Fronts) : ISocketPayload;

public sealed record FrontSocketPayload(FrontActiveReadModel Front) : ISocketPayload;

public sealed record FrontsSocketPayload(IReadOnlyList<FrontActiveReadModel> Fronts) : ISocketPayload;

public sealed record FrontIdSocketPayload(FrontId FrontId) : ISocketPayload;
