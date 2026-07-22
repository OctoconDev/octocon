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
using Interfold.Api.Services;

namespace Interfold.Api.Controllers.Base;

[ApiController]
[Authorize]
public abstract class InterfoldControllerBase : ControllerBase
{
    [FromServices]
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Authenticated <see cref="ScopedSystemId"/> for the current request;
    /// middleware guarantees presence.</summary>
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


    /// <summary>Header <c>X-Interfold-Idempotency-Key</c> if present, else a fresh GUID
    /// (unkeyed requests are each their own operation).</summary>
    protected IdempotencyKey GetIdempotencyKey()
    {
        var header = Request.Headers[InterfoldHeaders.IdempotencyKey].FirstOrDefault();
        return new(!string.IsNullOrWhiteSpace(header) ? header : Guid.NewGuid().ToString("N"));
    }

    /// <summary>400 <see cref="ErrorResponse"/> when <paramref name="target"/> is the
    /// current principal, else null. Uses <see cref="ScopedSystemId.RepresentsSameUserAs(SystemId)"/>
    /// so raw and same-region-scoped shapes both self-reject with the per-op
    /// <c>Cannot*Self</c> code rather than a downstream generic error.</summary>
    protected ErrorResponse? RejectIfSelf(SystemId target, string message, ErrorCode code)
        => PrincipalId.RepresentsSameUserAs(target)
            ? new ErrorResponse(message, code, HttpStatusCode.BadRequest)
            : null;

    /// <summary><see cref="FriendLookup"/> overload; username-kind never self-rejects
    /// here (registry hop needed) — the command handler's post-resolution guard covers it.</summary>
    protected ErrorResponse? RejectIfSelf(FriendLookup target, string message, ErrorCode code)
        => PrincipalId.RepresentsSameUserAs(target)
            ? new ErrorResponse(message, code, HttpStatusCode.BadRequest)
            : null;

    /// <summary>Prepends the server origin to <see cref="AvatarSource.Local"/> URLs only;
    /// external URLs pass through.</summary>
    protected AvatarUrl? QualifyAvatar(IAvatarBearing? bearing)
        => AvatarUrlQualifier.QualifyAvatar(bearing, Request.Scheme, Request.Host);

    /// <summary>Qualifies friend + fronting alter avatars in one call.</summary>
    protected FriendshipReadModel QualifyFriendship(FriendshipReadModel friendship)
        => AvatarUrlQualifier.QualifyFriendship(friendship, Request.Scheme, Request.Host);

    /// <summary>Qualifies the friend-request profile's avatar.</summary>
    protected FriendRequestReadModel QualifyFriendRequest(FriendRequestReadModel request)
        => AvatarUrlQualifier.QualifyFriendRequest(request, Request.Scheme, Request.Host);

    /// <summary>Returns the first file part from a multipart/form-data body. Non-multipart
    /// bodies, parse failures, and mid-read IOException yield a null stream; a zero-byte
    /// file flips <c>EmptyFilePart</c> so callers can distinguish "empty file" from
    /// "no file" (same wire shape, distinct error codes).</summary>
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

    protected async Task<Response> HandleAvatarUploadAsync(
        Func<CancellationToken, Task<IAvatarBearing>> getExistingAvatarAsync,
        Func<ScopedSystemId, Stream, CancellationToken, Task<AvatarUrl>> saveToStorageAsync,
        Func<AvatarUrl, CancellationToken, Task<Response>> updateMetadataAsync,
        IAvatarStorage avatarStorage,
        CancellationToken ct)
    {
        var principal = PrincipalId;
        var upload = await ResolveMultipartUploadAsync(ct);
        var avatarStream = upload.Stream;
        if (avatarStream is null)
        {
            if (upload.EmptyFilePart)
                return new ErrorResponse("Avatar file is empty.", ErrorCodes.AvatarFileEmpty, HttpStatusCode.BadRequest);

            return new ErrorResponse("No avatar file provided.", ErrorCodes.AvatarFileRequired, HttpStatusCode.BadRequest);
        }

        AvatarUrl avatarUrl;
        try
        {
            await using (avatarStream)
            {
                avatarUrl = await saveToStorageAsync(principal, avatarStream, ct);
            }
        }
        catch
        {
            return new ErrorResponse("An error occurred while uploading the file.", ErrorCodes.UnknownError, HttpStatusCode.InternalServerError);
        }

        // Shared post-save tail — see RunAvatarMetadataChangeAsync.
        return await RunAvatarMetadataChangeAsync(
            getExistingAvatarAsync,
            c => updateMetadataAsync(avatarUrl, c),
            avatarStorage,
            ct);
    }

    protected async Task<Response> HandleAvatarUrlUploadAsync(
        string? requestUrl,
        Func<CancellationToken, Task<IAvatarBearing>> getExistingAvatarAsync,
        Func<AvatarUrl, CancellationToken, Task<Response>> updateMetadataAsync,
        IAvatarStorage avatarStorage,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestUrl))
            return new ErrorResponse("Avatar URL payload required.", ErrorCodes.AvatarUrlInvalid, HttpStatusCode.BadRequest);

        if (!AvatarUrlValidator.TryNormalize(requestUrl, out var url, out var err))
            return new ErrorResponse("Invalid avatar URL.", err, HttpStatusCode.BadRequest);

        var normalisedUrl = new AvatarUrl(url);
        return await RunAvatarMetadataChangeAsync(
            getExistingAvatarAsync,
            c => updateMetadataAsync(normalisedUrl, c),
            avatarStorage,
            ct);
    }

    protected Task<Response> HandleAvatarDeleteAsync(
        Func<CancellationToken, Task<IAvatarBearing>> getExistingAvatarAsync,
        Func<CancellationToken, Task<Response>> updateMetadataAsync,
        IAvatarStorage avatarStorage,
        CancellationToken ct)
        => RunAvatarMetadataChangeAsync(getExistingAvatarAsync, updateMetadataAsync, avatarStorage, ct);

    /// <summary>Shared avatar-mutation tail: best-effort read current → update metadata
    /// → best-effort delete previous Local bytes on success. Read/delete errors are
    /// swallowed — a stale bytes-file isn't worth failing a metadata write over.</summary>
    private async Task<Response> RunAvatarMetadataChangeAsync(
        Func<CancellationToken, Task<IAvatarBearing>> getExistingAvatarAsync,
        Func<CancellationToken, Task<Response>> updateMetadataAsync,
        IAvatarStorage avatarStorage,
        CancellationToken ct)
    {
        AvatarUrl? currentAvatarUrl = null;
        AvatarSource? currentAvatarSource = null;
        try
        {
            var existingAvatar = await getExistingAvatarAsync(ct);
            currentAvatarUrl = existingAvatar.AvatarUrl;
            currentAvatarSource = existingAvatar.AvatarSource;
        }
        catch { }

        var result = await updateMetadataAsync(ct);
        if (!result.IsSuccess) return result;

        if (currentAvatarSource == AvatarSource.Local && currentAvatarUrl is not null)
        {
            try { await avatarStorage.DeleteByUrlAsync(currentAvatarUrl, ct); } catch { }
        }

        return result;
    }

    /// <summary>Executes a command handler with latency / outcome / conflict metrics and
    /// stamps <c>X-Interfold-Command-Id</c> on the response.</summary>
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

    /// <summary>Dispatches and maps to 204 on success, error on conflict.</summary>
    protected async Task<Response> DispatchNoContentAsync<TPayload, TResult>(
        ICommandHandler<TPayload, TResult> handler,
        OperationId operationId,
        TPayload payload,
        CancellationToken ct)
        where TResult : ICommandResult
    {
        var envelope = BuildEnvelope(operationId, payload);
        var result = await handler.HandleAsync(envelope, ct);
        return CommandNoContent(result);
    }

    /// <summary>Dispatches and maps to 201 with an async-hydrated body; optional
    /// <paramref name="locationSelector"/> stamps the Location header.</summary>
    protected async Task<Response<TData>> DispatchCreatedAsync<TPayload, TResult, TData>(
        ICommandHandler<TPayload, TResult> handler,
        OperationId operationId,
        TPayload payload,
        Func<TResult, Task<TData?>> dataSelector,
        CancellationToken ct,
        Func<TResult, string>? locationSelector = null)
        where TResult : ICommandResult
    {
        var envelope = BuildEnvelope(operationId, payload);
        var result = await handler.HandleAsync(envelope, ct);
        return await CommandCreatedAsync(result, dataSelector, locationSelector);
    }


    /// <summary>Dispatches and maps to 202 on success, error on conflict.</summary>
    protected async Task<Response<TData>> DispatchAcceptedAsync<TPayload, TResult, TData>(
        ICommandHandler<TPayload, TResult> handler,
        OperationId operationId,
        TPayload payload,
        Func<TResult, TData> dataSelector,
        CancellationToken ct)
        where TResult : ICommandResult
    {
        var envelope = BuildEnvelope(operationId, payload);
        var result = await handler.HandleAsync(envelope, ct);
        return CommandAccepted(result, dataSelector);
    }


    /// <summary>Maps a result to 204 or an error.</summary>
    protected Response CommandNoContent<T>(CommandExecutionResult<T> result)
    {
        if (result.Accepted)
            return new Response();

        return ConflictToError(result.Conflict!);
    }

    /// <summary>Maps a result to 201 carrying the selected body; Replay comes off the
    /// result directly.</summary>
    protected Response<TData> CommandCreated<T, TData>(CommandExecutionResult<T> result, Func<T, TData> dataSelector)
        where T : ICommandResult
    {
        if (result.Accepted)
            return new SuccessResponse<TData>(dataSelector(result.Result!), HttpStatusCode.Created, result.Result!.Replay);

        return ConflictToError(result.Conflict!);
    }

    /// <summary>Maps a result to 201 with async body hydration; a null hydration returns
    /// an unknown-error response. Optional Location header via
    /// <paramref name="locationSelector"/>.</summary>
    protected async Task<Response<TData>> CommandCreatedAsync<T, TData>(
        CommandExecutionResult<T> result,
        Func<T, Task<TData?>> dataSelector,
        Func<T, string>? locationSelector = null)
        where T : ICommandResult
    {
        if (!result.Accepted)
            return ConflictToError(result.Conflict!);

        var data = await dataSelector(result.Result!);
        if (data is null)
            return new ErrorResponse("An unknown error occurred.", ErrorCodes.UnknownError, HttpStatusCode.InternalServerError);

        if (locationSelector != null)
            Response.Headers.Location = locationSelector(result.Result!);

        return new SuccessResponse<TData>(data, HttpStatusCode.Created, result.Result!.Replay);
    }

    /// <summary>Maps a result to 202; controller returns as soon as dispatch lands, with
    /// the lifecycle continuing out-of-band (typically WebSocket frames).</summary>
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

    // Synthetic stamp for envelopes built from [AllowAnonymous] endpoints (OAuth callbacks);
    // the handler resolves the real system id off the payload and must never trust this.
    private static readonly ScopedSystemId AnonymousPrincipalId = ScopedSystemId.ParseScoped("nam:auth");

    /// <summary>Non-throwing <see cref="PrincipalId"/>; null on [AllowAnonymous] routes.</summary>
    private ScopedSystemId? TryGetPrincipalId()
        => HttpContext.Items.TryGetValue(InterfoldPrincipalMiddleware.PrincipalIdItemKey, out var value)
            && value is ScopedSystemId principal
            ? principal
            : null;

    /// <summary>Builds an envelope for the current request; anonymous routes get the
    /// synthetic <see cref="AnonymousPrincipalId"/> stamp.</summary>
    protected CommandEnvelope<TPayload> BuildEnvelope<TPayload>(OperationId operationId, TPayload payload)
        => new(operationId, Guid.NewGuid(),
               PrincipalId: TryGetPrincipalId() ?? AnonymousPrincipalId,
               IdempotencyKey: GetIdempotencyKey(),
               OccurredAt: TimeProvider.GetUtcNow(), Payload: payload);

    /// <summary>Dispatches and maps to 200 via <paramref name="wireSelector"/>.</summary>
    protected async Task<Response<TWire>> DispatchOkAsync<TPayload, TResult, TWire>(
        ICommandHandler<TPayload, TResult> handler,
        OperationId operationId,
        TPayload payload,
        Func<TResult, TWire> wireSelector,
        CancellationToken ct)
        where TResult : ICommandResult
    {
        var envelope = BuildEnvelope(operationId, payload);
        var result = await handler.HandleAsync(envelope, ct);
        if (result.Accepted)
            return new SuccessResponse<TWire>(wireSelector(result.Result!));

        return ConflictToError(result.Conflict!);
    }

    /// <summary>200 with <paramref name="value"/>, 404 with the given message/code when null.</summary>
    protected Response<T> OkOrNotFound<T>(T? value, string message, ErrorCode code)
        => value is null
            ? new ErrorResponse(message, code, HttpStatusCode.NotFound)
            : new SuccessResponse<T>(value);
}