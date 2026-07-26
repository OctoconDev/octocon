using Interfold.Shared.Contracts.Enums;

namespace Interfold.Socket.Contracts;

public sealed record PhoenixReplyPayload<TResponse>(PhoenixReplyStatus Status, TResponse Response) : ISocketPayload;
