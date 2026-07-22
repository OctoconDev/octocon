using Microsoft.AspNetCore.Mvc;
using System.Text;
using Interfold.Api.Helpers;
using Interfold.Api.Models;
using Interfold.Api.Services;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Alters;
using Interfold.Api.Controllers.Base;
using Interfold.Contracts;
using Interfold.Contracts.Validation;

namespace Interfold.Api.Controllers;

[Route("api/systems/me/alters")]
public sealed class AltersController : InterfoldControllerBase
{
    private readonly IAlterRepository _alterRepository;
    private readonly CreateAlterCommandHandler _createHandler;
    private readonly UpdateAlterCommandHandler _updateHandler;
    private readonly DeleteAlterCommandHandler _deleteHandler;
    private readonly IAvatarStorage _avatarStorage;

    public AltersController(
        IAlterRepository alterRepository,
        CreateAlterCommandHandler createHandler,
        UpdateAlterCommandHandler updateHandler,
        DeleteAlterCommandHandler deleteHandler,
        IAvatarStorage avatarStorage)
    {
        _alterRepository = alterRepository;
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _avatarStorage = avatarStorage;
    }

    [HttpGet]
    public async Task<Response<IReadOnlyList<AlterReadModel>>> List(CancellationToken ct)
    {
        var alters = await _alterRepository.ListAsync(PrincipalId, ct);
        return new SuccessResponse<IReadOnlyList<AlterReadModel>>(alters);
    }

    [HttpGet("{alterId:int}")]
    public async Task<Response<AlterReadModel>> Show([FromRoute][ValidAlterId] AlterId alterId, CancellationToken ct)
    {
        var alter = await _alterRepository.GetAsync(PrincipalId, alterId, ct);
        if (alter is not null)
        {
            alter.AvatarUrl = QualifyAvatar(alter);
        }
        return OkOrNotFound(alter, "Alter not found.", ErrorCodes.AlterNotFound);
    }

    [HttpPost]
    public async Task<Response<AlterReadModel>> Create([FromBody] CreateAlterRequest req, CancellationToken ct)
    {
        var principal = PrincipalId;
        var envelope = BuildEnvelope(OperationIds.AlterCreate, new CreateAlterCommand(req.Name, TimeProvider.GetUtcNow()));

        return await CommandCreatedAsync(
            await _createHandler.HandleAsync(envelope, ct),
            async (res) => await _alterRepository.GetAsync(principal, res.AlterId, ct),
            locationSelector: res => $"/api/systems/me/alters/{res.AlterId}"
        );
    }

    [HttpPatch("{alterId:int}")]
    public async Task<Response> Update([FromRoute][ValidAlterId] AlterId alterId, [FromBody] UpdateAlterRequest req, CancellationToken ct)
    {
        var fields = req.Fields?.Select(f => new AlterFieldCommand(f.Id, f.Value)).ToList();

        var payload = new UpdateAlterCommand
        {
            AlterId = alterId,
            Name = req.Name,
            Description = req.Description,
            Color = req.Color,
            Pronouns = req.Pronouns,
            SecurityLevel = req.SecurityLevel,
            Fields = fields,
            ProxyName = req.ProxyName,
            Alias = req.Alias,
            Untracked = req.Untracked,
            Archived = req.Archived,
            Pinned = req.Pinned,
            UpdatedAt = TimeProvider.GetUtcNow(),
        };

        return await DispatchNoContentAsync(_updateHandler, OperationIds.AlterUpdate, payload, ct);
    }

    [HttpDelete("{alterId:int}")]
    public async Task<Response> Delete([FromRoute][ValidAlterId] AlterId alterId, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_deleteHandler, OperationIds.AlterDelete, new DeleteAlterCommand(alterId)
        , ct);
    }

    [HttpPut("{alterId:int}/avatar")]
    [Consumes("multipart/form-data")]
    public async Task<Response> UploadAvatarMultipart([FromRoute][ValidAlterId] AlterId alterId, CancellationToken ct)
    {
        return await HandleAvatarUploadAsync(
            async (c) => await _alterRepository.GetAsync(PrincipalId, alterId, c),
            async (principal, stream, c) => await _avatarStorage.SaveAlterAvatarAsync(principal, alterId, stream, c),
            async (url, c) => CommandNoContent(await _updateHandler.HandleAsync(BuildEnvelope(OperationIds.AlterAvatarUpload, new UpdateAlterCommand
            {
                AlterId = alterId,
                AvatarUrl = url,
                AvatarSource = AvatarSource.Local,
                UpdatedAt = TimeProvider.GetUtcNow(),
            }), c)),
            _avatarStorage,
            ct);
    }

    /// <summary>
    /// JSON sibling of <see cref="UploadAvatarMultipart"/>: stores the supplied URL on
    /// <c>avatar_url</c> with <c>avatar_source = External</c> without fetching the bytes.
    /// </summary>
    [HttpPut("{alterId:int}/avatar")]
    [Consumes("application/json")]
    public async Task<Response> UploadAvatarByUrl([FromRoute][ValidAlterId] AlterId alterId, [FromBody] AvatarUrlUploadRequest req, CancellationToken ct)
    {
        if (req is null)
            return new ErrorResponse("Avatar URL payload required.", ErrorCodes.AvatarUrlInvalid, System.Net.HttpStatusCode.BadRequest);

        return await HandleAvatarUrlUploadAsync(
            req.Url.Value,
            async (c) => await _alterRepository.GetAsync(PrincipalId, alterId, c),
            async (url, c) => CommandNoContent(await _updateHandler.HandleAsync(BuildEnvelope(OperationIds.AlterAvatarUpload, new UpdateAlterCommand
            {
                AlterId = alterId,
                AvatarUrl = url,
                AvatarSource = AvatarSource.External,
                UpdatedAt = TimeProvider.GetUtcNow(),
            }), c)),
            _avatarStorage,
            ct);
    }

    [HttpDelete("{alterId:int}/avatar")]
    public async Task<Response> DeleteAvatar([FromRoute][ValidAlterId] AlterId alterId, CancellationToken ct)
    {
        return await HandleAvatarDeleteAsync(
            async (c) => await _alterRepository.GetAsync(PrincipalId, alterId, c),
            async (c) => CommandNoContent(await _updateHandler.HandleAsync(BuildEnvelope(OperationIds.AlterAvatarDelete, new UpdateAlterCommand
            {
                AlterId = alterId,
                UpdatedAt = TimeProvider.GetUtcNow(),
                ClearAvatar = true,
            }), c)),
            _avatarStorage,
            ct);
    }
}
