using Interfold.Contracts.Ids;

namespace Interfold.Contracts;

public sealed record SystemIdSocketPayload(SystemId SystemId) : ISocketPayload;
