using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts.Models.Commands;

// RecoveryCode's converter emits the raw string, so persisted command JSON and
// idempotency hashes are unchanged; the property name is frozen.
public sealed record SetupEncryptionCommand(RecoveryCode RecoveryCode);

public sealed record RecoverEncryptionCommand(RecoveryCode RecoveryCode);

public sealed record ResetEncryptionCommand();
