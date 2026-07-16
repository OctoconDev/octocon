using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.ImportOperations;

namespace Interfold.Domain.Abstractions.ImportJobs;

/// <summary>
/// A unit of work for the asynchronous import worker. Materialised by
/// <c>ImportSpCommandHandler</c> / <c>ImportPkCommandHandler</c> after they successfully
/// claim a per-system import slot, then handed to <see cref="IImportJobQueue"/>.
///
/// <para>
/// The token + recovery code travel through this record. They never leave the process —
/// the queue is in-process, the worker resolves the same DI graph as the handler, and the
/// runner immediately consumes them. We do not persist secrets to the
/// <c>import_operations</c> table.
/// </para>
/// </summary>
/// <param name="OperationId">The operation row's id, used by the worker to update status and publish events.</param>
/// <param name="SystemId">Octocon system id, kept in its regional-prefixed shape so repositories and event publishers can use it directly.</param>
/// <param name="Kind">Third-party integration — picks the runner.</param>
/// <param name="Token">Caller-supplied third-party API token (SP token or PK token). Sensitive — never log.</param>
/// <param name="RecoveryCode">Optional SP recovery code, plaintext after the controller's <c>TryResolveRecoveryCode</c> decryption. Null for PK and for SP without recovery. Sensitive — never log.</param>
public sealed record ImportJobItem(
    ImportOperationId OperationId,
    ScopedSystemId SystemId,
    ImportOperationKind Kind,
    ImportToken Token,
    RecoveryCode? RecoveryCode);
