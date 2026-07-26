using Interfold.Shared.Contracts.Ids;

namespace Interfold.Socket.Contracts;

public sealed record SystemIdSocketPayload(SystemId SystemId) : ISocketPayload;
