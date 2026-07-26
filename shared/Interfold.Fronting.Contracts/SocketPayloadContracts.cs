using Interfold.Fronting.Contracts.Ids;
using Interfold.Fronting.Contracts.Models.Read;
using Interfold.Socket.Contracts;

namespace Interfold.Fronting.Contracts;

public sealed record SocketBatchedFrontsPayload(int BatchIndex, int TotalBatches, IReadOnlyList<FrontActiveReadModel> Fronts) : ISocketPayload;

public sealed record FrontSocketPayload(FrontActiveReadModel Front) : ISocketPayload;

public sealed record FrontsSocketPayload(IReadOnlyList<FrontActiveReadModel> Fronts) : ISocketPayload;

public sealed record FrontIdSocketPayload(FrontId FrontId) : ISocketPayload;
