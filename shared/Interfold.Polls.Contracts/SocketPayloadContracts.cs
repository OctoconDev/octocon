using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;

namespace Interfold.Shared.Contracts;

public sealed record PollSocketPayload(PollReadModel Poll) : ISocketPayload;

public sealed record PollDeletedSocketPayload(PollId PollId) : ISocketPayload;
