using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts;

public sealed record SystemIdSocketPayload(SystemId SystemId) : ISocketPayload;
