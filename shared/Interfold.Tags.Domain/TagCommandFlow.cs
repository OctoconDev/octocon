using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Shared.Domain.Tags;

internal static class TagCommandFlow
{
    public static CommandExecutionResult<TagCommandResult> Success(SystemId systemId, TagId tagId)
        => CommandExecutionResult<TagCommandResult>.Success(new TagCommandResult(systemId, tagId, Replay: false));

    public static async Task<CommandExecutionResult<TagCommandResult>> ExecuteCreateAsync(
        CommandEnvelope<CreateTagCommand> command,
        IClusterEventBus eventBus,
        Func<CancellationToken, Task<TagId?>> createAsync,
        CancellationToken cancellationToken = default)
    {
        var createdTagId = await createAsync(cancellationToken);
        if (createdTagId is null)
            return CommandHandler.RejectInvariant<TagCommandResult>(command.OperationId, EntityRefs.TagCreateFailed);

        await eventBus.PublishAsync(new TagCreatedEvent(command.PrincipalId, createdTagId.Value), cancellationToken);
        return Success(command.PrincipalId, createdTagId.Value);
    }

    public static async Task<CommandExecutionResult<TagCommandResult>?> RejectIfAlterNotFoundAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        AlterId alterId,
        IAlterExistenceCheck alterExistence,
        CancellationToken cancellationToken = default)
    {
        var alterExists = await alterExistence.ExistsAsync(command.PrincipalId, alterId, cancellationToken);
        return alterExists
            ? null
            : CommandHandler.RejectInvariant<TagCommandResult>(command.OperationId, EntityRefs.TagAlterNotFound);
    }

    public static async Task<CommandExecutionResult<TagCommandResult>> ExecuteTagMutationAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        TagId tagId,
        Func<CancellationToken, Task<bool>> mutateAsync,
        EntityRef notFoundEntityRef,
        IClusterEventBus eventBus,
        CancellationToken cancellationToken = default)
        => await ExecuteTagMutationAsync(
            command,
            tagId,
            mutateAsync,
            notFoundEntityRef,
            ct => eventBus.PublishAsync(new TagUpdatedEvent(command.PrincipalId, tagId), ct),
            cancellationToken);

    public static async Task<CommandExecutionResult<TagCommandResult>> ExecuteTagMutationAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        TagId tagId,
        Func<CancellationToken, Task<bool>> mutateAsync,
        EntityRef notFoundEntityRef,
        Func<CancellationToken, ValueTask> publishAccepted,
        CancellationToken cancellationToken = default)
    {
        var mutated = await mutateAsync(cancellationToken);
        if (!mutated)
            return CommandHandler.RejectInvariant<TagCommandResult>(command.OperationId, notFoundEntityRef);

        await publishAccepted(cancellationToken);
        return Success(command.PrincipalId, tagId);
    }
}