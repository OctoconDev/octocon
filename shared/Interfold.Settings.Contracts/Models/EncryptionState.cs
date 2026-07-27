using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts.Models;

public sealed record EncryptionState(bool Initialized, KeyChecksum? KeyChecksum, EncryptionSalt? Salt);
