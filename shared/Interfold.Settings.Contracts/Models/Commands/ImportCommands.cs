using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.Commands;

public sealed record ImportPkCommand(ImportToken Token);

public sealed record ImportSpCommand(ImportToken Token, RecoveryCode? RecoveryCode);
