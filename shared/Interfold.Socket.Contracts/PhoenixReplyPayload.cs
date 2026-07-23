using Interfold.Shared.Contracts.Enums;

namespace Interfold.Shared.Contracts;

public sealed record PhoenixReplyPayload<TResponse>(PhoenixReplyStatus Status, TResponse Response) : ISocketPayload;
