using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Read;

namespace Interfold.Contracts;

public sealed record PollSocketPayload(PollReadModel Poll) : ISocketPayload;

public sealed record PollDeletedSocketPayload(PollId PollId) : ISocketPayload;
