using Interfold.Contracts.Enums;

namespace Interfold.Contracts;

public sealed record PhoenixReplyPayload<TResponse>(PhoenixReplyStatus Status, TResponse Response) : ISocketPayload;
