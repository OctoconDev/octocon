using Interfold.Fronting.Contracts;
using Interfold.Fronting.Contracts.Ids;
using Interfold.Fronting.Contracts.Models.Commands;
using Interfold.Fronting.Contracts.Models.Read;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Fronting.Domain;

internal static class FrontingCommandFlow
{
    public sealed record FrontByIdResolution(FrontActiveReadModel? Active, FrontHistoryReadModel? History)
    {
        public bool WasActive => Active is not null;

        public AlterId AlterId => Active?.Front.AlterId ?? History!.AlterId;
    }

    public static CommandExecutionResult<FrontCommandResult>? RejectIfInvalidFrontId<TCommand>(
        CommandEnvelope<TCommand> command,
        FrontId frontId)
        => frontId == FrontId.Empty
            ? CommandHandler.RejectInvariant<FrontCommandResult>(command.OperationId, EntityRefs.FrontingInvalidFrontId)
            : null;

    public static CommandExecutionResult<FrontCommandResult>? RejectIfInvalidComment<TCommand>(
        CommandEnvelope<TCommand> command,
        string? comment)
        => !FrontId.IsValidComment(comment)
            ? CommandHandler.RejectInvariant<FrontCommandResult>(command.OperationId, EntityRefs.FrontingInvalidComment)
            : null;

    public static CommandExecutionResult<FrontCommandResult>? RejectIfInvalidAlterId<TCommand>(
        CommandEnvelope<TCommand> command,
        AlterId alterId)
        => CommandHandler.RejectIfAlterIdOutOfRange<FrontCommandResult>(
            command.OperationId,
            alterId,
            EntityRefs.FrontingInvalidAlterId);

    public static CommandExecutionResult<FrontCommandResult>? RejectIfAnyInvalidAlterId<TCommand>(
        CommandEnvelope<TCommand> command,
        IEnumerable<AlterId> alterIds)
        => alterIds.Any(alterId => alterId.Value < 1)
            ? CommandHandler.RejectInvariant<FrontCommandResult>(command.OperationId, EntityRefs.FrontingInvalidAlterId)
            : null;

    public static CommandExecutionResult<FrontCommandResult> Success(
        ScopedSystemId systemId,
        AlterId? alterId,
        FrontId? frontId)
        => CommandExecutionResult<FrontCommandResult>.Success(new FrontCommandResult(systemId, alterId, frontId, Replay: false));

    public static CommandExecutionResult<FrontCommandResult>? RejectIfMutationFailed<TCommand>(
        CommandEnvelope<TCommand> command,
        bool succeeded,
        EntityRef failedEntityRef)
        => !succeeded
            ? CommandHandler.RejectInvariant<FrontCommandResult>(command.OperationId, failedEntityRef)
            : null;

    public static async Task<CommandExecutionResult<FrontCommandResult>?> ExecuteMutationOrRejectAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        Func<CancellationToken, Task<bool>> mutateAsync,
        EntityRef failedEntityRef,
        CancellationToken cancellationToken = default)
    {
        var succeeded = await mutateAsync(cancellationToken);
        return RejectIfMutationFailed(command, succeeded, failedEntityRef);
    }

    public static (FrontId? FrontId, CommandExecutionResult<FrontCommandResult>? Rejection) GetStartedFrontIdOrReject<TCommand>(
        CommandEnvelope<TCommand> command,
        FrontId? startedFrontId)
        => startedFrontId is null
            ? (null, CommandHandler.RejectInvariant<FrontCommandResult>(command.OperationId, EntityRefs.FrontingStartFailed))
            : (startedFrontId, null);

    public static async Task<(FrontActiveReadModel? Active, CommandExecutionResult<FrontCommandResult>? Rejection)> GetActiveFrontByIdOrRejectAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        IFrontingRepository frontingRepository,
        FrontId frontId,
        CancellationToken cancellationToken = default)
    {
        var active = await frontingRepository.GetActiveByFrontIdAsync(command.PrincipalId, frontId, cancellationToken);
        if (active is null)
        {
            return (null, CommandHandler.RejectInvariant<FrontCommandResult>(command.OperationId, EntityRefs.FrontingNoFront));
        }

        return (active, null);
    }

    public static async Task<(FrontActiveReadModel? Active, CommandExecutionResult<FrontCommandResult>? Rejection)> GetActiveFrontByIdAfterValidationOrRejectAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        IFrontingRepository frontingRepository,
        FrontId frontId,
        CancellationToken cancellationToken = default)
    {
        if (RejectIfInvalidFrontId(command, frontId) is { } invalidFrontIdReject)
            return (null, invalidFrontIdReject);

        return await GetActiveFrontByIdOrRejectAsync(command, frontingRepository, frontId, cancellationToken);
    }

    public static async Task<(FrontByIdResolution? Resolution, CommandExecutionResult<FrontCommandResult>? Rejection)> ResolveFrontByIdOrRejectAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        IFrontingRepository frontingRepository,
        FrontId frontId,
        CancellationToken cancellationToken = default)
    {
        var active = await frontingRepository.GetActiveByFrontIdAsync(command.PrincipalId, frontId, cancellationToken);
        if (active is not null)
            return (new FrontByIdResolution(active, null), null);

        var history = await frontingRepository.GetHistoryEntryByFrontIdAsync(command.PrincipalId, frontId, cancellationToken);
        if (history is null)
        {
            return (null, CommandHandler.RejectInvariant<FrontCommandResult>(command.OperationId, EntityRefs.FrontingNoFront));
        }

        return (new FrontByIdResolution(null, history), null);
    }

    public static async Task<(FrontByIdResolution? Resolution, CommandExecutionResult<FrontCommandResult>? Rejection)> ResolveFrontByIdAfterValidationOrRejectAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        IFrontingRepository frontingRepository,
        FrontId frontId,
        CancellationToken cancellationToken = default)
    {
        if (RejectIfInvalidFrontId(command, frontId) is { } invalidFrontIdReject)
            return (null, invalidFrontIdReject);

        return await ResolveFrontByIdOrRejectAsync(command, frontingRepository, frontId, cancellationToken);
    }

    public static async Task<CommandExecutionResult<FrontCommandResult>?> RejectIfNotFrontingAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        IFrontingRepository frontingRepository,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var fronting = await frontingRepository.IsFrontingAsync(command.PrincipalId, alterId, cancellationToken);
        return fronting
            ? null
            : CommandHandler.RejectInvariant<FrontCommandResult>(command.OperationId, EntityRefs.FrontingNotFronting);
    }

    public static async Task<CommandExecutionResult<FrontCommandResult>?> RejectIfInvalidOrNotFrontingAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        IFrontingRepository frontingRepository,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        if (RejectIfInvalidAlterId(command, alterId) is { } invalidAlterIdReject)
            return invalidAlterIdReject;

        return await RejectIfNotFrontingAsync(command, frontingRepository, alterId, cancellationToken);
    }

    public static async Task<CommandExecutionResult<FrontCommandResult>?> RejectIfAlreadyFrontingAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        IFrontingRepository frontingRepository,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var alreadyFronting = await frontingRepository.IsFrontingAsync(command.PrincipalId, alterId, cancellationToken);
        return alreadyFronting
            ? CommandHandler.RejectInvariant<FrontCommandResult>(command.OperationId, EntityRefs.FrontingAlreadyFronting)
            : null;
    }

    public static async Task EndAltersBestEffortAsync(
        IFrontingRepository frontingRepository,
        ScopedSystemId systemId,
        IEnumerable<AlterId> alterIds,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken = default)
    {
        foreach (var alterId in alterIds)
        {
            // EndAsync returns false if there is no active row (race/no-op); intentionally ignore.
            await frontingRepository.EndAsync(systemId, alterId, endedAt, cancellationToken);
        }
    }

    public static async Task StartAltersIfNotFrontingAsync(
        IFrontingRepository frontingRepository,
        ScopedSystemId systemId,
        IEnumerable<FrontStartItem> startItems,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        foreach (var item in startItems)
        {
            var alreadyFronting = await frontingRepository.IsFrontingAsync(systemId, item.AlterId, cancellationToken);
            if (!alreadyFronting)
                await frontingRepository.StartAsync(systemId, item.AlterId, item.Comment, startedAt, cancellationToken);
        }
    }
}