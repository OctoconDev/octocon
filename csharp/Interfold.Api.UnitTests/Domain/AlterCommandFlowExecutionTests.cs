using Interfold.Contracts;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Alters;

namespace Interfold.Api.UnitTests.Domain;

/// <summary>
/// Regression harness for the <see cref="UpdateAlterCommandHandler"/> reject
/// branches — specifically the AlterNotFound path, which had zero direct
/// coverage before this file. PR #12 review comment #15.
///
/// <para>
/// The invariant these tests pin is the same "don't leak downstream side effects
/// when validation fails" contract that
/// <see cref="PollCommandFlowExecutionTests"/> pins for <c>PollCommandFlow</c>:
/// when the alter doesn't exist, the handler must not call <c>UpdateAsync</c>
/// and must not publish <c>AlterUpdatedEvent</c> on the cluster event bus. A
/// future regression that flattens the reject-early check (e.g. by evaluating
/// mutate/publish arguments eagerly) would silently produce spurious writes
/// and events that HTTP-level tests wouldn't catch because the handler still
/// returns the correct <c>alter:not_found</c> conflict.
/// </para>
/// </summary>
public sealed class AlterCommandFlowExecutionTests
{
    private static readonly ScopedSystemId Principal =
        ScopedSystemId.Compose(ScyllaKeyspace.Nam, "altrflw");

    private static readonly AlterId AnyAlterId = new(5);

    /// <summary>
    /// The core coverage gap #15 flagged: an update against a non-existent alter
    /// must short-circuit before touching <c>UpdateAsync</c> and before publishing
    /// <c>AlterUpdatedEvent</c>. ExistsAsync itself must be called exactly once so
    /// the reject path is provable.
    /// </summary>
    [Test]
    public async Task UpdateAlter_MissingAlter_RejectsWithNotFoundAndDoesNotMutateOrPublish()
    {
        var repo = new CountingAlterRepository { Exists = false };
        var bus = new CountingEventBus();
        var handler = new UpdateAlterCommandHandler(repo, new NoopIdempotencyStore(), bus);

        var command = NewEnvelope(NewMutatingPayload("renamed"));

        var result = await handler.HandleAsync(command);

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsFalse()
                .Because("A missing alter must be rejected, never accepted.");
            await Assert.That(result.Conflict?.EntityRef).IsEqualTo(EntityRefs.AlterNotFound)
                .Because("The rejection must be the not-found invariant, not a mutation-failed variant.");
            await Assert.That(repo.ExistsCalls).IsEqualTo(1)
                .Because("The existence check itself must run exactly once so the reject path is provable.");
            await Assert.That(repo.UpdateCalls).IsEqualTo(0)
                .Because("A not-found alter must never reach UpdateAsync — an eager-Task regression on ExecuteMutationAsync would raise this count.");
            await Assert.That(bus.PublishCalls).IsEqualTo(0)
                .Because("A not-found alter must never publish AlterUpdatedEvent — same eager-Task regression class.");
            await Assert.That(repo.AliasTakenCalls).IsEqualTo(0)
                .Because("The alias-collision probe is downstream of the existence check and must not run against a missing row.");
        }
    }

    /// <summary>
    /// The alter exists, but the repository update races and returns <c>false</c>.
    /// The handler must reject with <c>alter:update_failed</c>, must have called
    /// <c>UpdateAsync</c> exactly once, and must NOT publish (there is no
    /// accepted state to broadcast). Symmetrical to the equivalent Poll assertion
    /// so both flows keep matching invariants.
    /// </summary>
    [Test]
    public async Task UpdateAlter_ExistsButMutationReturnsFalse_RejectsWithUpdateFailedAndDoesNotPublish()
    {
        var repo = new CountingAlterRepository { Exists = true, UpdateResult = false };
        var bus = new CountingEventBus();
        var handler = new UpdateAlterCommandHandler(repo, new NoopIdempotencyStore(), bus);

        var command = NewEnvelope(NewMutatingPayload("renamed"));

        var result = await handler.HandleAsync(command);

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsFalse();
            await Assert.That(result.Conflict?.EntityRef).IsEqualTo(EntityRefs.AlterUpdateFailed);
            await Assert.That(repo.UpdateCalls).IsEqualTo(1);
            await Assert.That(bus.PublishCalls).IsEqualTo(0)
                .Because("A mutation that reports no change must not surface an Updated event.");
        }
    }

    /// <summary>
    /// Happy path: existing alter, successful update, no alias in payload → the
    /// repository is called once, the event bus is called once, the result is
    /// accepted. Pins that the reject branches above haven't accidentally been
    /// wired to catch the accept case too.
    /// </summary>
    [Test]
    public async Task UpdateAlter_HappyPath_CallsMutateOncePublishesOnceAndAccepts()
    {
        var repo = new CountingAlterRepository { Exists = true, UpdateResult = true };
        var bus = new CountingEventBus();
        var handler = new UpdateAlterCommandHandler(repo, new NoopIdempotencyStore(), bus);

        var command = NewEnvelope(NewMutatingPayload("renamed"));

        var result = await handler.HandleAsync(command);

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsTrue();
            await Assert.That(repo.UpdateCalls).IsEqualTo(1);
            await Assert.That(bus.PublishCalls).IsEqualTo(1);
        }
    }

    // -----------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------

    private static UpdateAlterCommand NewMutatingPayload(string newName) =>
        new()
        {
            AlterId = AnyAlterId,
            Name = newName,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static CommandEnvelope<UpdateAlterCommand> NewEnvelope(UpdateAlterCommand payload) =>
        new(
            OperationId: OperationIds.AlterUpdate,
            CommandId: Guid.NewGuid(),
            PrincipalId: Principal,
            IdempotencyKey: new IdempotencyKey(Guid.NewGuid().ToString("N")),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload);

    /// <summary>
    /// Counting fake for <see cref="IAlterRepository"/> — only the members the
    /// UpdateAlterCommandHandler path can reach are implemented; the rest throw
    /// so a future test that accidentally exercises an unrelated method fails
    /// loudly rather than silently returning defaults.
    /// </summary>
    private sealed class CountingAlterRepository : IAlterRepository
    {
        public bool Exists { get; set; }
        public bool UpdateResult { get; set; }
        public int ExistsCalls;
        public int UpdateCalls;
        public int AliasTakenCalls;

        public Task<bool> ExistsAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ExistsCalls);
            return Task.FromResult(Exists);
        }

        public Task<bool> UpdateAsync(SystemId systemId, UpdateAlterCommand command, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref UpdateCalls);
            return Task.FromResult(UpdateResult);
        }

        public Task<bool> AliasTakenByOtherAsync(SystemId systemId, AlterId alterId, string alias, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref AliasTakenCalls);
            return Task.FromResult(false);
        }

        public Task<AlterId?> CreateAsync(SystemId systemId, CreateAlterCommand command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("CreateAsync is not reachable from the UpdateAlterCommandHandler path.");

        public Task<bool> DeleteAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("DeleteAsync is not reachable from the UpdateAlterCommandHandler path.");

        public Task<IReadOnlyList<AlterReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ListAsync is not reachable from the UpdateAlterCommandHandler path.");

        public Task<IReadOnlyList<BareAlter>> ListGuardedAsync(SystemId systemId, SystemId? viewerSystemId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ListGuardedAsync is not reachable from the UpdateAlterCommandHandler path.");

        public Task<AlterReadModel?> GetAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("GetAsync is not reachable from the UpdateAlterCommandHandler path.");

        public Task<BareAlter?> GetGuardedAsync(SystemId systemId, AlterId alterId, SystemId? viewerSystemId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("GetGuardedAsync is not reachable from the UpdateAlterCommandHandler path.");
    }

    private sealed class CountingEventBus : IClusterEventBus
    {
        public int PublishCalls;

        public ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : class
        {
            Interlocked.Increment(ref PublishCalls);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(ScopedSystemId? targetSystemId, CancellationToken ct = default) where TEvent : class
            => AsyncEnumerable.Empty<TEvent>();
    }

    private sealed class NoopIdempotencyStore : IIdempotencyStore
    {
        public Task<IdempotencyMatch?> FindAsync(
            SystemId principalId,
            OperationId operationId,
            IdempotencyKey idempotencyKey,
            CancellationToken cancellationToken = default) => Task.FromResult<IdempotencyMatch?>(null);

        public Task SaveAsync(
            SystemId principalId,
            OperationId operationId,
            IdempotencyKey idempotencyKey,
            string payloadHash,
            string outcomeHash,
            string? outcomePayload,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static class AsyncEnumerable
    {
        public static IAsyncEnumerable<T> Empty<T>() => new EmptyAsyncEnumerable<T>();

        private sealed class EmptyAsyncEnumerable<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
        {
            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
            public T Current => default!;
            public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
