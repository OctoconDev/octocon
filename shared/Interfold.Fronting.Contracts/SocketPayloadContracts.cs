using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Read;

namespace Interfold.Contracts;

public sealed record SocketBatchedFrontsPayload(int BatchIndex, int TotalBatches, IReadOnlyList<FrontActiveReadModel> Fronts) : ISocketPayload;

public sealed record FrontSocketPayload(FrontActiveReadModel Front) : ISocketPayload;

public sealed record FrontsSocketPayload(IReadOnlyList<FrontActiveReadModel> Fronts) : ISocketPayload;

public sealed record FrontIdSocketPayload(FrontId FrontId) : ISocketPayload;
