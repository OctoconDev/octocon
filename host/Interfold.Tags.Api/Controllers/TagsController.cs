using Interfold.Api.Models;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.Read;
using Microsoft.AspNetCore.Mvc;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Domain.Tags;
using Interfold.Api.Controllers.Base;
using Interfold.Shared.Contracts;

namespace Interfold.Api.Controllers;

[Route("api/systems/me/tags")]
public sealed class TagsController : InterfoldControllerBase
{
   private readonly ITagRepository _tagRepository;
   private readonly CreateTagCommandHandler _create;
   private readonly UpdateTagCommandHandler _update;
   private readonly DeleteTagCommandHandler _delete;
   private readonly AttachAlterToTagCommandHandler _attachAlter;
   private readonly DetachAlterFromTagCommandHandler _detachAlter;
   private readonly SetParentTagCommandHandler _setParent;
   private readonly RemoveParentTagCommandHandler _removeParent;

   public TagsController(
        ITagRepository tagRepository,
       CreateTagCommandHandler create,
       UpdateTagCommandHandler update,
       DeleteTagCommandHandler delete,
       AttachAlterToTagCommandHandler attachAlter,
       DetachAlterFromTagCommandHandler detachAlter,
       SetParentTagCommandHandler setParent,
       RemoveParentTagCommandHandler removeParent)
    {
       _tagRepository = tagRepository;
       _create = create;
       _update = update;
       _delete = delete;
       _attachAlter = attachAlter;
       _detachAlter = detachAlter;
       _setParent = setParent;
       _removeParent = removeParent;
    }

    [HttpPost]
    public async Task<Response<TagReadModel>> CreateTag(
        [FromBody] CreateTagRequest body,
        CancellationToken cancellationToken)
    {
        var principal = PrincipalId;
        // CreateTagCommandHandler owns InsertedAtUtc on the public-API path and derives it
        // from the envelope's OccurredAt right before calling the repo. Passing `default`
        // here keeps the hashed payload stable across retries with the same idempotency key
        // (otherwise every call would stamp a fresh DateTime.UtcNow and look like a
        // different request, triggering ConflictDuplicate on every replay).
        return await DispatchCreatedAsync(
            _create,
            OperationIds.TagCreate,
            new CreateTagCommand(body.Name, body.ParentTagId, InsertedAtUtc: default),
            async (res) => await _tagRepository.GetAsync(principal, res.TagId, cancellationToken),
            cancellationToken);
    }

    [HttpPatch("{id}")]
    public async Task<Response> UpdateTag(TagId id, [FromBody] UpdateTagRequest body, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_update, OperationIds.TagUpdate, new UpdateTagCommand(id, body.Name, body.Color, body.Description, body.SecurityLevel)
        , ct);
    }

    [HttpDelete("{id}")]
    public async Task<Response> DeleteTag(TagId id, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_delete, OperationIds.TagDelete, new DeleteTagCommand(id)
        , ct);
    }

    [HttpPost("{id}/alter")]
    public async Task<Response> AttachAlter(TagId id, [FromBody] TagAlterRequest body, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_attachAlter, OperationIds.TagAttachAlter, new AttachAlterToTagCommand(id, body.AlterId)
        , ct);
    }

    [HttpDelete("{id}/alter")]
    public async Task<Response> DetachAlter(TagId id, [FromBody] TagAlterRequest body, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_detachAlter, OperationIds.TagDetachAlter, new DetachAlterFromTagCommand(id, body.AlterId)
        , ct);
    }

    [HttpPost("{id}/parent")]
    public async Task<Response> SetParent(TagId id, [FromBody] SetParentRequest body, CancellationToken ct)
    {
        if (body.ParentTagId is not { } parentTagId || parentTagId == TagId.Empty)
            return new ErrorResponse("Invalid parent tag ID.", ErrorCodes.InvalidParentTagId, System.Net.HttpStatusCode.BadRequest);

        return await DispatchNoContentAsync(_setParent, OperationIds.TagSetParent, new SetParentTagCommand(id, parentTagId)
        , ct);
    }

    [HttpDelete("{id}/parent")]
    public async Task<Response> RemoveParent(TagId id, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_removeParent, OperationIds.TagRemoveParent, new RemoveParentTagCommand(id)
        , ct);
    }
}
