using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;

namespace Interfold.Shared.Contracts;

public sealed record AlterSocketPayload(AlterReadModel Alter) : ISocketPayload;

public sealed record AlterDeletedSocketPayload(AlterId AlterId) : ISocketPayload;

public sealed record AlterIdSocketPayload(AlterId? AlterId) : ISocketPayload;
