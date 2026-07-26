using Interfold.Polls.Contracts.Ids;
using Interfold.Polls.Contracts.Models.Read;
using Interfold.Socket.Contracts;

namespace Interfold.Polls.Contracts;

public sealed record PollSocketPayload(PollReadModel Poll) : ISocketPayload;

public sealed record PollDeletedSocketPayload(PollId PollId) : ISocketPayload;
