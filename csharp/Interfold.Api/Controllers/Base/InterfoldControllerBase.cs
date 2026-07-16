using System.Diagnostics;
using System.Net;
using Interfold.Api.Helpers;
using Interfold.Api.Middleware;
using Interfold.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Contracts;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;

namespace Interfold.Api.Controllers.Base;

[ApiController]
[Authorize]
public abstract class InterfoldControllerBase : ControllerBase
{
    /// <summary>
    /// The authenticated principal for the current request as a
    /// <see cref="ScopedSystemId"/>. The middleware guarantees the value is present and
    /// scoped — any endpoint reaching this getter has already passed the JWT-sub
    /// validation. <see cref="ScopedSystemId"/> widens implicitly to
    /// <see cref="SystemId"/> at every persistence / repository call site, so this
    /// getter's byte value flows unchanged to Postgres idempotency and Scylla PKs.
    /// </summary>
    protected ScopedSystemId PrincipalId
    {
        get
        {
            if (HttpContext.Items.TryGetValue(InterfoldPrincipalMiddleware.PrincipalIdItemKey, out var value)
                && value is ScopedSystemId principal)
            {
                return principal;
            }

            throw new InvalidOperationException(
                "PrincipalId is unavailable. Ensure InterfoldPrincipalMiddleware is configured.");
        }
    }


    /// <summary>
    /// Resolves the idempotency key for the current request: the
    /// <c>X-Interfold-Idempotency-Key</c> header when present, otherwise a fresh GUID
    /// (each unkeyed request is its own operation). The header is the only client-supplied
    /// source — payload-level keys were removed.
    /// </summary>
    protected IdempotencyKey GetIdempotencyKey()
    {
        var header = Request.Headers[InterfoldHeaders.IdempotencyKey].FirstOrDefault();
        return new(!string.IsNullOrWhiteSpace(header) ? header : Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Source-aware avatar qualification: prepends the server origin only when the avatar
    /// is locally hosted (<see cref="AvatarSource.Local"/>). External URLs pass through
    /// verbatim; a null / blank input returns unchanged. Non-avatar callers that need
    /// origin qualification without the <see cref="AvatarSource"/> discriminator can call
    /// <c>AvatarUrlQualifier.Qualify(string?, string, HostString)</c> directly.
    /// </summary>
    protected AvatarUrl? QualifyAvatar(AvatarUrl? url, AvatarSource? source)
        => AvatarUrlQualifier.QualifyAvatar(url, source, Request.Scheme, Request.Host);

    /// <summary>
    /// Reads a multipart/form-data request body and returns the first file part as an
    /// <see cref="AvatarUploadPayload"/>. Non-multipart requests, empty bodies, boundary
    /// parse failures, and mid-read <see cref="IOException"/>s all resolve to
    /// <see cref="AvatarUploadPayload"/> with a <see langword="null"/> stream — the
    /// caller is expected to treat this as "no upload landed" and NOT dereference the
    /// stream. A file part that lands with a zero-byte body flips
    /// <see cref="AvatarUploadPayload.EmptyFilePart"/> to <see langword="true"/> so the
    /// caller can distinguish "client attached an empty file" from "client attached no
    /// file at all" (the two are the same 415-adjacent shape on the wire but distinct
    /// error codes on the response).
    ///
    /// <para>
    /// Shared by <c>AltersController.UploadAvatar</c> and
    /// <c>SettingsController.UploadAvatar</c> so a future tweak to multipart parsing
    /// (a size cap, a MIME allow-list, an <c>Ampersand.NetworkOnlyRequestBody</c>
    /// substitution during test setup) lands exactly once. Both callers pass
    /// <see cref="HttpContext.RequestAborted"/> as <paramref name="ct"/>.
    /// </para>
    /// </summary>
    protected async Task<AvatarUploadPayload> ResolveMultipartUploadAsync(CancellationToken ct)
    {
        var emptyFilePart = false;

        if (Request.Body is null)
            return new AvatarUploadPayload(null, emptyFilePart);

        Request.EnableBuffering();

        if (Request.Body.CanSeek)
            Request.Body.Position = 0;

        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var mediaType)
            || !mediaType.MediaType.HasValue
            || !mediaType.MediaType.Value.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return new AvatarUploadPayload(null, emptyFilePart);
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
            return new AvatarUploadPayload(null, emptyFilePart);

        try
        {
            var reader = new MultipartReader(boundary, Request.Body);
            MultipartSection? section;

            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                    continue;

                var fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value
                               ?? HeaderUtilities.RemoveQuotes(disposition.FileName).Value;

                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    var payload = new MemoryStream();
                    await section.Body.CopyToAsync(payload, ct);
                    if (payload.Length <= 0)
                    {
                        emptyFilePart = true;
                        await payload.DisposeAsync();
                        continue;
                    }

                    payload.Position = 0;
                    return new AvatarUploadPayload(payload, emptyFilePart);
                }
            }
        }
        catch (IOException)
        {
            return new AvatarUploadPayload(null, emptyFilePart);
        }

        return new AvatarUploadPayload(null, emptyFilePart);
    }

    /// <summary>
    /// Executes a command handler with:
    /// <list type="bullet">
    ///   <item>Latency measurement recorded in <see cref="InterfoldMetrics.CommandLatencyMs"/>.</item>
    ///   <item>Outcome counted in <see cref="InterfoldMetrics.CommandsTotal"/> (accepted / replay / rejected).</item>
    ///   <item>Conflict counted in <see cref="InterfoldMetrics.ConflictsTotal"/> when applicable.</item>
    ///   <item><c>X-Interfold-Command-Id</c> response header set from <paramref name="envelope"/>.</item>
    /// </list>
    /// </summary>
    protected async Task<IActionResult> ExecuteCommandAsync<TPayload, TResult>(
        ICommandHandler<TPayload, TResult> handler,
        CommandEnvelope<TPayload> envelope,
        CancellationToken ct)
        where TResult : ICommandResult
    {
        var sw = Stopwatch.StartNew();
        var result = await handler.HandleAsync(envelope, ct);
        sw.Stop();

        var opTag = new KeyValuePair<string, object?>("operation_id", envelope.OperationId);

        InterfoldMetrics.CommandLatencyMs.Record(
            sw.Elapsed.TotalMilliseconds,
            opTag);

        if (result.Accepted)
        {
            var outcome = result.Result!.Replay ? "replay" : "accepted";
            InterfoldMetrics.CommandsTotal.Add(1, opTag,
                new KeyValuePair<string, object?>("outcome", outcome));
        }
        else
        {
            InterfoldMetrics.CommandsTotal.Add(1, opTag,
                new KeyValuePair<string, object?>("outcome", "rejected"));

            InterfoldMetrics.ConflictsTotal.Add(1, opTag,
                new KeyValuePair<string, object?>("conflict_code",
                    result.Conflict!.Code.ToString()));
        }

        Response.Headers[InterfoldHeaders.CommandId] = envelope.CommandId.ToString("N");

        if (result.Accepted)
            return Ok(result.Result);

        return result.Conflict!.Code switch
        {
            ConflictCode.ConflictDuplicate    => Conflict(result.Conflict),
            ConflictCode.ConflictInvariant    => UnprocessableEntity(result.Conflict),
            _                                 => StatusCode(500, new ErrorResponse(
                "An unknown error occurred.",
                ErrorCodes.UnknownError,
                System.Net.HttpStatusCode.InternalServerError))
        };
    }

    /// <summary>
    /// Maps a <see cref="CommandExecutionResult{T}"/> to a 204 No Content <see cref="Response{NoContent}"/>
    /// on success, or an <see cref="ErrorResponse"/> on failure.
    /// </summary>
    protected Response CommandNoContent<T>(CommandExecutionResult<T> result)
    {
        if (result.Accepted)
            return new Response();

        return ConflictToError(result.Conflict!);
    }

    /// <summary>
    /// Maps a <see cref="CommandExecutionResult{T}"/> to a 201 Created <see cref="Response{TData}"/>
    /// carrying the mapped <paramref name="dataSelector"/> result, or an <see cref="ErrorResponse"/> on failure.
    /// </summary>
    protected Response<TData> CommandCreated<T, TData>(CommandExecutionResult<T> result, Func<T, TData> dataSelector, Func<T?, bool?>? replaySelector = null)
    {
        if (result.Accepted)
            return new SuccessResponse<TData>(dataSelector(result.Result!), HttpStatusCode.Created, replaySelector?.Invoke(result.Result));

        return ConflictToError(result.Conflict!);
    }

    /// <summary>
    /// Maps a <see cref="CommandExecutionResult{T}"/> to a 202 Accepted <see cref="Response{TData}"/>
    /// carrying the mapped <paramref name="dataSelector"/> result. Used by endpoints whose
    /// command handler dispatches work asynchronously (the lifecycle then continues out-of-band,
    /// typically via WebSocket frames) — the controller is done as soon as the dispatch lands.
    /// </summary>
    protected Response<TData> CommandAccepted<T, TData>(CommandExecutionResult<T> result, Func<T, TData> dataSelector)
    {
        if (result.Accepted)
            return new SuccessResponse<TData>(dataSelector(result.Result!), HttpStatusCode.Accepted);

        return ConflictToError(result.Conflict!);
    }

    protected ErrorResponse ConflictToError(Contracts.Operations.ConflictResult conflict)
    {
        Response.Headers[InterfoldHeaders.OperationId] = conflict.OperationId.Value;
        
        return conflict.Code switch
        {
            // ResolutionHint doubles as the client-visible error code; ToWire keeps the
            // exact legacy strings ("no_retry" / "manual_merge_required") on the wire.
            ConflictCode.ConflictDuplicate => new ErrorResponse(
                "A duplicate conflict occurred.", new ErrorCode(conflict.ResolutionHint.ToWire()), HttpStatusCode.Conflict, conflict.EntityRef.Value),
            ConflictCode.ConflictInvariant => new ErrorResponse(
                "The request could not be processed due to a conflict.", new ErrorCode(conflict.ResolutionHint.ToWire()),
                HttpStatusCode.UnprocessableEntity, conflict.EntityRef.Value),
            _ => new ErrorResponse("An unknown error occurred.", ErrorCodes.UnknownError, HttpStatusCode.InternalServerError, conflict.EntityRef.Value)
        };
    }
}