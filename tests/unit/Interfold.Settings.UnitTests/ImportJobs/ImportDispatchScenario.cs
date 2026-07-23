using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.ImportOperations;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.ImportJobs;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Coordination;
using Interfold.Infrastructure.InMemory.Repository;

namespace Interfold.Api.UnitTests.ImportJobs;

// Shared harness for the SP / PK ICommandHandler dispatch tests. Owns the wiring
// (in-memory repo + in-process queue + CapturingQueue) so per-kind test bodies only
// call DispatchAsync and assert.
internal sealed class ImportDispatchScenario<TCommand> : IAsyncDisposable
    where TCommand : notnull
{
    private readonly Func<string, string, CommandEnvelope<TCommand>> _envelopeFactory;

    public ImportDispatchScenario(
        Func<IImportOperationRepository, IImportJobQueue, ICommandHandler<TCommand, ImportDispatchCommandResult>> handlerFactory,
        Func<string, string, CommandEnvelope<TCommand>> envelopeFactory)
    {
        var repo = new InMemoryImportOperationRepository();
        Queue = new InProcessImportJobQueue();
        Capture = new CapturingQueue(Queue);
        Handler = handlerFactory(repo, Capture);
        _envelopeFactory = envelopeFactory;
    }

    public InProcessImportJobQueue Queue { get; }
    public CapturingQueue Capture { get; }
    public ICommandHandler<TCommand, ImportDispatchCommandResult> Handler { get; }

    public Task<CommandExecutionResult<ImportDispatchCommandResult>> DispatchAsync(
        string idempotencyKey = "idem-1",
        string token = "synthetic-token")
        => Handler.HandleAsync(_envelopeFactory(idempotencyKey, token));

    public ValueTask DisposeAsync() => Queue.DisposeAsync();
}

internal static class ImportDispatchScenario
{
    public static readonly ScopedSystemId SpSystemId
        = ScopedSystemId.ParseScoped("nam:sys-sp-dispatch-test");

    public static readonly ScopedSystemId PkSystemId
        = ScopedSystemId.ParseScoped("nam:sys-pk-dispatch-test");

    public static ImportDispatchScenario<ImportSpCommand> ForSp()
        => new(
            (repo, queue) => new Interfold.Shared.Domain.Settings.ImportSpCommandHandler(repo, queue),
            (key, token) => NewSpEnvelope(SpSystemId, key, token));

    public static ImportDispatchScenario<ImportPkCommand> ForPk()
        => new(
            (repo, queue) => new Interfold.Shared.Domain.Settings.ImportPkCommandHandler(repo, queue),
            (key, token) => NewPkEnvelope(PkSystemId, key, token));

    public static CommandEnvelope<ImportSpCommand> NewSpEnvelope(ScopedSystemId systemId, string idempotencyKey, string token) => new(
        OperationId: new("settings:import_sp"),
        CommandId: Guid.NewGuid(),
        PrincipalId: systemId,
        IdempotencyKey: new(idempotencyKey),
        OccurredAt: DateTimeOffset.UtcNow,
        Payload: new ImportSpCommand(new(token), RecoveryCode: null));

    public static CommandEnvelope<ImportPkCommand> NewPkEnvelope(ScopedSystemId systemId, string idempotencyKey, string token) => new(
        OperationId: new("settings:import_pk"),
        CommandId: Guid.NewGuid(),
        PrincipalId: systemId,
        IdempotencyKey: new(idempotencyKey),
        OccurredAt: DateTimeOffset.UtcNow,
        Payload: new ImportPkCommand(new(token)));
}
