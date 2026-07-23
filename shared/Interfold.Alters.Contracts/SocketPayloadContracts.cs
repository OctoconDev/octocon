using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;

namespace Interfold.Contracts;

public sealed record AlterSocketPayload(AlterReadModel Alter) : ISocketPayload;

public sealed record AlterDeletedSocketPayload(AlterId AlterId) : ISocketPayload;

public sealed record AlterIdSocketPayload(AlterId? AlterId) : ISocketPayload;
