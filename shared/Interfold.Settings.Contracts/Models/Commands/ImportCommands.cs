using Interfold.Settings.Contracts.Ids;

namespace Interfold.Settings.Contracts.Models.Commands;

public sealed record ImportPkCommand(ImportToken Token);

public sealed record ImportSpCommand(ImportToken Token, RecoveryCode? RecoveryCode);
