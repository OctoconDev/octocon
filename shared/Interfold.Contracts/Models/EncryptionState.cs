using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models;

public sealed record EncryptionState(bool Initialized, KeyChecksum? KeyChecksum, EncryptionSalt? Salt);