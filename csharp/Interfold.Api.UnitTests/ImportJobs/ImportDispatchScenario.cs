using Interfold.Contracts;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.ImportOperations;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.ImportJobs;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Coordination;
using Interfold.Infrastructure.InMemory.Repository;

namespace Interfold.Api.UnitTests.ImportJobs;

/// <summary>
/// Shared harness for the SP / PK <see cref="ICommandHandler{TCommand,TResult}"/> dispatch
/// tests. The two <c>Import*CommandHandlerDispatchTests</c> classes drive the same three
/// scenarios (fresh dispatch, concurrent-collapse dedupe, empty-token rejection) but must
/// stay per-kind so each side keeps its kind-specific assertions (e.g. PK asserts
/// <c>RecoveryCode is null</c>). This scenario owns the wiring: an in-memory operation
/// repository, an in-process job queue wrapped by <see cref="CapturingQueue"/>, and a
/// caller-supplied handler factory + envelope factory. Test bodies then call
/// <see cref="DispatchAsync"/> and assert against the returned result and the exposed
/// <see cref="Capture"/> collection — no per-file <c>NewHandler</c> / <c>NewEnvelope</c>
/// tuples to keep in lockstep.
/// </summary>
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

    /// <summary>
    /// Invokes the handler with a fresh envelope built from
    /// (<paramref name="idempotencyKey"/>, <paramref name="token"/>). Returns the handler's
    /// <see cref="CommandExecutionResult{T}"/> so callers can assert Accepted / Result /
    /// Status against their kind's specific expectations.
    /// </summary>
    public Task<CommandExecutionResult<ImportDispatchCommandResult>> DispatchAsync(
        string idempotencyKey = "idem-1",
        string token = "synthetic-token")
        => Handler.HandleAsync(_envelopeFactory(idempotencyKey, token));

    public ValueTask DisposeAsync() => Queue.DisposeAsync();
}

/// <summary>
/// Convenience factories for the two production handler shapes so the SP / PK test files
/// don't have to hand-instantiate the repository + queue + capture triple; centralising the
/// wiring means a future change to the handler constructor surface touches one line here
/// instead of two twin file bodies.
/// </summary>
internal static class ImportDispatchScenario
{
    public static readonly ScopedSystemId SpSystemId
        = ScopedSystemId.ParseScoped("nam:sys-sp-dispatch-test");

    public static readonly ScopedSystemId PkSystemId
        = ScopedSystemId.ParseScoped("nam:sys-pk-dispatch-test");

    public static ImportDispatchScenario<ImportSpCommand> ForSp()
        => new(
            (repo, queue) => new Interfold.Domain.Settings.ImportSpCommandHandler(repo, queue),
            (key, token) => NewSpEnvelope(SpSystemId, key, token));

    public static ImportDispatchScenario<ImportPkCommand> ForPk()
        => new(
            (repo, queue) => new Interfold.Domain.Settings.ImportPkCommandHandler(repo, queue),
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
