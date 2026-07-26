using Interfold.Alters.Contracts.Models;
using Interfold.Shared.Contracts.Ids;
using Interfold.Socket.Contracts;

namespace Interfold.Alters.Contracts;

public sealed record AlterSocketPayload(AlterReadModel Alter) : ISocketPayload;

public sealed record AlterDeletedSocketPayload(AlterId AlterId) : ISocketPayload;

public sealed record AlterIdSocketPayload(AlterId? AlterId) : ISocketPayload;
