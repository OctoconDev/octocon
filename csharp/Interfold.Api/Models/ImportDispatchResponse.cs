using Interfold.Contracts.Models.ImportOperations;
using Interfold.Contracts.Ids;

namespace Interfold.Api.Models;

/// <summary>
/// Body returned by the asynchronous-import endpoints (<c>POST /api/settings/import-sp</c>
/// and <c>POST /api/settings/import-pk</c>) on a successful dispatch. The HTTP status is
/// always <c>202 Accepted</c>; the actual import lifecycle is then driven over the
/// WebSocket with the existing <c>sp_import_complete</c> / <c>sp_import_failed</c>
/// (or <c>pk_*</c>) frames.
///
/// <para>
/// The Compose frontend at
/// <c>app/octocon/app/ui/compose/screens/main/settings/SettingsRootScreen.kt</c> already
/// ignores the body on success and listens to the WebSocket for the terminal frame — so
/// this DTO is primarily for operator visibility and any future client that wants the
/// correlation handle. We still ship the values so the contract is self-documenting and
/// integration tests can assert on them.
/// </para>
/// </summary>
/// <param name="OperationId">Server-minted TimeUuid surrogate for this dispatch. Same value flows through the <c>import_operations</c> Cassandra row.</param>
/// <param name="Status">Either <see cref="ImportOperationDispatchStatus.Queued"/> (this dispatch claimed a fresh slot) or <see cref="ImportOperationDispatchStatus.Running"/> (an import was already in flight for the system and this dispatch collapsed onto it). Serialises as <c>"queued"</c>/<c>"running"</c>.</param>
/// <param name="StartedAt">When the dispatcher observed the claim. For a collapsed dispatch this is the observation time of the *second* request, not the original — clients that care about the true import start should read it from the eventual completion frame.</param>
public sealed record ImportDispatchResponse(
    ImportOperationId OperationId,
    ImportOperationDispatchStatus Status,
    DateTimeOffset StartedAt);
