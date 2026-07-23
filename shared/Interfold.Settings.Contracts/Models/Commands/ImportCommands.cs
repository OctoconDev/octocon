using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts.Models.Commands;

public sealed record ImportPkCommand(ImportToken Token);

public sealed record ImportSpCommand(ImportToken Token, RecoveryCode? RecoveryCode);
