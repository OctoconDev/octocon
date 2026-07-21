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
    /// Returns a 400 <see cref="ErrorResponse"/> naming <paramref name="code"/> when
    /// <paramref name="target"/> refers to the current principal; otherwise
    /// <see langword="null"/>. Wraps
    /// <see cref="ScopedSystemId.RepresentsSameUserAs(SystemId)"/> so raw and
    /// same-region-scoped shapes both self-reject — a bare byte compare would miss the
    /// raw-route case and let a request slip through with the generic downstream error
    /// instead of the per-op <c>Cannot*Self</c> code.
    /// </summary>
    /// <remarks>
    /// Callers use the null-conditional pattern so the guard reads as a fast return:
    /// <code>
    /// if (RejectIfSelf(id, "You cannot friend yourself.", ErrorCodes.CannotSendSelf) is { } reject)
    ///     return reject;
    /// </code>
    /// Only usable when the self-reject shape is a 400 <c>BadRequest</c>. Endpoints that
    /// reject self-access with a 403 (<c>PublicSystemsController</c>) or that fold the
    /// self-check into a shared parameterised helper (<c>FriendsController.SetTrustInternal</c>)
    /// keep hand-rolling the check.
    /// </remarks>
    protected ErrorResponse? RejectIfSelf(SystemId target, string message, ErrorCode code)
        => PrincipalId.RepresentsSameUserAs(target)
            ? new ErrorResponse(message, code, HttpStatusCode.BadRequest)
            : null;

    /// <summary>
    /// <see cref="FriendLookup"/> overload for endpoints whose route binds a lookup
    /// (id-or-username) instead of a bare <see cref="SystemId"/>. <c>Kind.Id</c>
    /// delegates to the <see cref="SystemId"/> primitive; <c>Kind.Username</c> always
    /// returns <see langword="false"/> because deciding "is username X me?" requires a
    /// registry hop — the downstream command handler's post-resolution guard takes over
    /// for that case.
    /// </summary>
    protected ErrorResponse? RejectIfSelf(FriendLookup target, string message, ErrorCode code)
        => PrincipalId.RepresentsSameUserAs(target)
            ? new ErrorResponse(message, code, HttpStatusCode.BadRequest)
            : null;

    /// <summary>
    /// Source-aware avatar qualification for any <see cref="IAvatarBearing"/> read model:
    /// prepends the server origin only when the avatar is locally hosted
    /// (<see cref="AvatarSource.Local"/>). External URLs pass through verbatim; a null
    /// bearing / blank input returns unchanged. Non-avatar callers that need origin
    /// qualification without the <see cref="AvatarSource"/> discriminator can call
    /// <c>AvatarUrlQualifier.Qualify(string?, string, HostString)</c> directly.
    /// </summary>
    protected AvatarUrl? QualifyAvatar(IAvatarBearing? bearing)
        => AvatarUrlQualifier.QualifyAvatar(bearing, Request.Scheme, Request.Host);

    /// <summary>
    /// Convenience: qualifies both the friend's avatar and every fronting alter's avatar
    /// against the current request's origin. Callers use this instead of hand-rolling the
    /// <c>friendship with { Friend = ..., Fronting = ... }</c> record-update expression at
    /// every friendship-shaped endpoint.
    /// </summary>
    protected FriendshipReadModel QualifyFriendship(FriendshipReadModel friendship)
        => AvatarUrlQualifier.QualifyFriendship(friendship, Request.Scheme, Request.Host);

    /// <summary>
    /// Convenience: qualifies the requester/requestee profile's avatar against the
    /// current request's origin. Callers use this instead of the
    /// <c>x with { System = x.System with { AvatarUrl = QualifyAvatar(...) } }</c>
    /// lambda inside <c>Select</c>.
    /// </summary>
    protected FriendRequestReadModel QualifyFriendRequest(FriendRequestReadModel request)
        => AvatarUrlQualifier.QualifyFriendRequest(request, Request.Scheme, Request.Host);

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

        // Post-save tail (read-current → update-metadata → best-effort cleanup of old
        // Local bytes) is identical to the URL / delete flows — delegate through the
        // shared helper via the same closure trick HandleAvatarUrlUploadAsync uses so a
        // future change to the cleanup rules (e.g. also purging External URLs, retry
        // policy on the delete probe) only has to be made in one spot.
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

    /// <summary>
    /// Shared post-save / no-save tail for every avatar mutation
    /// (<see cref="HandleAvatarUploadAsync"/> after the multipart bytes have already
    /// landed in storage, <see cref="HandleAvatarUrlUploadAsync"/> for the External-URL
    /// path, <see cref="HandleAvatarDeleteAsync"/> for the clear-metadata path): read
    /// the current avatar (best-effort; failures here don't abort the mutation), run
    /// the metadata update, and if the mutation succeeded AND the previous avatar was
    /// locally hosted, best-effort delete the old bytes from storage. Errors from
    /// either the "read current" probe or the "delete old" cleanup are intentionally
    /// swallowed — a stale bytes-file isn't worth failing an otherwise-successful
    /// metadata write over.
    /// </summary>
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
    /// Builds a command envelope, handles it, and maps it to a 204 No Content response on success,
    /// or maps conflicts to error responses.
    /// </summary>
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

    /// <summary>
    /// Builds a command envelope, handles it, and maps it to a 201 Created response by
    /// fetching the created entity asynchronously on success, or maps conflicts to error
    /// responses. Pass <paramref name="locationSelector"/> to emit a <c>Location</c>
    /// header pointing at the created resource; omit it for endpoints that don't expose
    /// a canonical URL for the row.
    /// </summary>
    /// <remarks>
    /// The sync (<c>Func&lt;TResult, TData&gt;</c>) overload this method used to sit
    /// alongside was dropped because no controller called it — the create paths all
    /// need an async repository round-trip to hydrate the response body. Callers whose
    /// response IS the command result directly should use <see cref="CommandCreated{T,TData}"/>
    /// with their own <c>HandleAsync</c> call (see <c>FrontingController.StartFront</c>).
    /// </remarks>
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


    /// <summary>
    /// Builds a command envelope, handles it, and maps it to a 202 Accepted response on success,
    /// or maps conflicts to error responses.
    /// </summary>
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
    /// carrying the mapped <paramref name="dataSelector"/> result, or an <see cref="ErrorResponse"/>
    /// on failure. The Replay flag is read straight off the result via
    /// <see cref="ICommandResult.Replay"/> — callers no longer thread a selector.
    /// </summary>
    protected Response<TData> CommandCreated<T, TData>(CommandExecutionResult<T> result, Func<T, TData> dataSelector)
        where T : ICommandResult
    {
        if (result.Accepted)
            return new SuccessResponse<TData>(dataSelector(result.Result!), HttpStatusCode.Created, result.Result!.Replay);

        return ConflictToError(result.Conflict!);
    }

    /// <summary>
    /// Maps a <see cref="CommandExecutionResult{T}"/> to a 201 Created <see cref="Response{TData}"/>
    /// by asynchronously fetching the created entity via <paramref name="dataSelector"/>. Returns an
    /// <see cref="ErrorResponse"/> on conflict or if the entity fetch returns null.
    /// Optionally sets the Location header if <paramref name="locationSelector"/> is provided.
    /// The Replay flag is read straight off the result via
    /// <see cref="ICommandResult.Replay"/> — callers no longer thread a selector.
    /// </summary>
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

    /// <summary>
    /// Synthetic principal id stamped on envelopes built from an <c>[AllowAnonymous]</c>
    /// endpoint (currently the OAuth auth / auth-link callbacks). The command handler on
    /// the other side resolves the real system id off the payload itself — an anonymous
    /// endpoint must never read <see cref="CommandEnvelope{T}.PrincipalId"/> for
    /// authorisation. See <see cref="AuthController"/> / <see cref="AuthLinkController"/>
    /// callbacks.
    /// </summary>
    private static readonly ScopedSystemId AnonymousPrincipalId = ScopedSystemId.ParseScoped("nam:auth");

    /// <summary>
    /// Non-throwing <see cref="PrincipalId"/> — returns the middleware-populated
    /// principal when present, or <see langword="null"/> for endpoints that reach
    /// dispatch without an authenticated caller (only <c>[AllowAnonymous]</c> routes
    /// legitimately land here). Wrapped by <see cref="BuildEnvelope{TPayload}"/> so
    /// callers get a synthetic <see cref="AnonymousPrincipalId"/> stamp instead of the
    /// <c>PrincipalId is unavailable</c> throw the strict accessor produces.
    /// </summary>
    private ScopedSystemId? TryGetPrincipalId()
        => HttpContext.Items.TryGetValue(InterfoldPrincipalMiddleware.PrincipalIdItemKey, out var value)
            && value is ScopedSystemId principal
            ? principal
            : null;

    /// <summary>
    /// Builds a <see cref="CommandEnvelope{TPayload}"/> for the current request. On
    /// authenticated endpoints the envelope carries the middleware-populated principal;
    /// on <c>[AllowAnonymous]</c> routes it falls back to <see cref="AnonymousPrincipalId"/>
    /// so the callback controllers no longer have to hand-construct envelopes purely to
    /// dodge <see cref="PrincipalId"/>'s throw. The synthetic principal is safe because
    /// the handlers behind anonymous endpoints resolve the real system id off the
    /// payload (link token, OAuth identity) rather than trusting the envelope.
    /// </summary>
    protected CommandEnvelope<TPayload> BuildEnvelope<TPayload>(OperationId operationId, TPayload payload)
        => new(operationId, Guid.NewGuid(),
               PrincipalId: TryGetPrincipalId() ?? AnonymousPrincipalId,
               IdempotencyKey: GetIdempotencyKey(),
               OccurredAt: TimeProvider.GetUtcNow(), Payload: payload);

    /// <summary>
    /// Builds a command envelope, handles it, and maps it to a 200 OK response by
    /// projecting the command result into a wire-shaped response body via
    /// <paramref name="wireSelector"/>. Callers use this when the command's on-the-wire
    /// response body is a wrapper record around a single computed field
    /// (see <c>SettingsController.SetupEncryption</c> / <c>RecoverEncryption</c>) — the
    /// alternative was a hand-rolled per-endpoint dispatcher that inlined the accepted /
    /// conflict split every time.
    /// </summary>
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

    /// <summary>
    /// Show-endpoint helper: returns a 404 <see cref="ErrorResponse"/> naming
    /// <paramref name="message"/> / <paramref name="code"/> when <paramref name="value"/>
    /// is <see langword="null"/>; otherwise wraps the value in a 200 <see cref="SuccessResponse{T}"/>.
    /// <para>
    /// Consolidates the "load + null-check + 404 vs 200" ternary that every read-model
    /// Show endpoint hand-rolled. Sites that mutate the loaded entity before returning
    /// (avatar URL qualification etc.) apply the mutation under an
    /// <c>if (v is not null)</c> guard and then call this helper with the (possibly
    /// mutated) value.
    /// </para>
    /// </summary>
    protected Response<T> OkOrNotFound<T>(T? value, string message, ErrorCode code)
        => value is null
            ? new ErrorResponse(message, code, HttpStatusCode.NotFound)
            : new SuccessResponse<T>(value);
}