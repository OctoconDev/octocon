using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.ImportOperations;

namespace Interfold.Shared.Domain.Abstractions.ImportJobs;

/// <summary>A unit of work for the async import worker. Handler → in-process queue →
/// runner; secrets travel through this record but never persist to
/// <c>import_operations</c>.</summary>
/// <param name="Token">Third-party API token. Sensitive — never log.</param>
/// <param name="RecoveryCode">Optional SP recovery code, plaintext post-decryption.
/// Null for PK / SP-without-recovery. Sensitive — never log.</param>
public sealed record ImportJobItem(
    ImportOperationId OperationId,
    ScopedSystemId SystemId,
    ImportOperationKind Kind,
    ImportToken Token,
    RecoveryCode? RecoveryCode);
