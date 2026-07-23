using Interfold.Contracts;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Polls;

namespace Interfold.Api.UnitTests.Domain;

// Regression harness for PollCommandFlow.ExecuteExistingPollMutationAsync. Guards
// against a prior eager-Task footgun where UpdateAsync/DeleteAsync and PublishAsync
// fired against a not-found row before ExistsAsync completed — invisible to HTTP-level
// tests but observable in a spurious write + published event. Helper now takes
// Func<CT, ...>; these tests count calls to catch a re-regression at the seam.
public sealed class PollCommandFlowExecutionTests
{
    private static readonly ScopedSystemId Principal =
        ScopedSystemId.Compose(ScyllaKeyspace.Nam, "poll-flow-tests");

    private static readonly PollId AnyPollId = new(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    [Test]
    public async Task DeletePoll_MissingPoll_DoesNotMutateOrPublish()
    {
        var repo = new CountingPollRepository { Exists = false };
        var bus = new CountingEventBus();
        var handler = new DeletePollCommandHandler(repo, new NoopIdempotencyStore(), bus);

        var command = NewEnvelope(OperationIds.PollDelete, new DeletePollCommand(AnyPollId));

        var result = await handler.HandleAsync(command);

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsFalse()
                .Because("A missing poll must be rejected with poll:not_found, not accepted.");
            await Assert.That(result.Conflict?.EntityRef).IsEqualTo(EntityRefs.PollNotFound)
                .Because("The rejection must be the not-found invariant, not a mutation-failed variant.");
            await Assert.That(repo.DeleteCalls).IsEqualTo(0)
                .Because("A not-found poll must never reach DeleteAsync — the eager-Task footgun would raise this count.");
            await Assert.That(bus.PublishCalls).IsEqualTo(0)
                .Because("A not-found poll must never publish PollDeletedEvent — the eager-Task footgun would raise this count.");
            await Assert.That(repo.ExistsCalls).IsEqualTo(1)
                .Because("The existence check itself must run exactly once so the reject path is provable.");
        }
    }

    [Test]
    public async Task DeletePoll_ExistsButMutationReturnsFalse_RejectsWithDeleteFailedAndDoesNotPublish()
    {
        var repo = new CountingPollRepository { Exists = true, DeleteResult = false };
        var bus = new CountingEventBus();
        var handler = new DeletePollCommandHandler(repo, new NoopIdempotencyStore(), bus);

        var command = NewEnvelope(OperationIds.PollDelete, new DeletePollCommand(AnyPollId));

        var result = await handler.HandleAsync(command);

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsFalse();
            await Assert.That(result.Conflict?.EntityRef).IsEqualTo(EntityRefs.PollDeleteFailed);
            await Assert.That(repo.DeleteCalls).IsEqualTo(1);
            await Assert.That(bus.PublishCalls).IsEqualTo(0)
                .Because("A mutation that reports no change must not surface a Deleted event.");
        }
    }

    [Test]
    public async Task DeletePoll_HappyPath_CallsMutateOncePublishesOnceAndAccepts()
    {
        var repo = new CountingPollRepository { Exists = true, DeleteResult = true };
        var bus = new CountingEventBus();
        var handler = new DeletePollCommandHandler(repo, new NoopIdempotencyStore(), bus);

        var command = NewEnvelope(OperationIds.PollDelete, new DeletePollCommand(AnyPollId));

        var result = await handler.HandleAsync(command);

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsTrue();
            await Assert.That(repo.DeleteCalls).IsEqualTo(1);
            await Assert.That(bus.PublishCalls).IsEqualTo(1);
        }
    }

    // Update shares the helper with Delete so it inherits the same footgun; pinned
    // separately so drift in one branch cannot silently pass in the other.
    [Test]
    public async Task UpdatePoll_MissingPoll_DoesNotMutateOrPublish()
    {
        var repo = new CountingPollRepository { Exists = false };
        var bus = new CountingEventBus();
        var handler = new UpdatePollCommandHandler(repo, new NoopIdempotencyStore(), bus);

        // Populate one mutable field so the handler doesn't short-circuit at
        // PollCommandValidation.HasNoMutableFields.
        var payload = new UpdatePollCommand(
            Id: AnyPollId,
            Title: "renamed",
            Description: null,
            TimeEnd: null,
            HasTimeEnd: false,
            Data: null);
        var command = NewEnvelope(OperationIds.PollUpdate, payload);

        var result = await handler.HandleAsync(command);

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsFalse();
            await Assert.That(result.Conflict?.EntityRef).IsEqualTo(EntityRefs.PollNotFound);
            await Assert.That(repo.UpdateCalls).IsEqualTo(0)
                .Because("A not-found poll must never reach UpdateAsync — the eager-Task footgun would raise this count.");
            await Assert.That(bus.PublishCalls).IsEqualTo(0)
                .Because("A not-found poll must never publish PollUpdatedEvent — the eager-Task footgun would raise this count.");
        }
    }

    private static CommandEnvelope<T> NewEnvelope<T>(OperationId opId, T payload) =>
        new(
            OperationId: opId,
            CommandId: Guid.NewGuid(),
            PrincipalId: Principal,
            IdempotencyKey: new IdempotencyKey(Guid.NewGuid().ToString("N")),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload);

    private sealed class CountingPollRepository : IPollRepository
    {
        public bool Exists { get; set; }
        public bool DeleteResult { get; set; }
        public bool UpdateResult { get; set; }
        public int ExistsCalls;
        public int DeleteCalls;
        public int UpdateCalls;

        public Task<IReadOnlyList<PollReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PollReadModel>>(Array.Empty<PollReadModel>());

        public Task<PollReadModel?> GetAsync(SystemId systemId, PollId pollId, CancellationToken cancellationToken = default)
            => Task.FromResult<PollReadModel?>(null);

        public Task<PollId?> CreateAsync(SystemId systemId, CreatePollCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult<PollId?>(null);

        public Task<bool> ExistsAsync(SystemId systemId, PollId pollId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ExistsCalls);
            return Task.FromResult(Exists);
        }

        public Task<bool> UpdateAsync(SystemId systemId, UpdatePollCommand command, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref UpdateCalls);
            return Task.FromResult(UpdateResult);
        }

        public Task<bool> DeleteAsync(SystemId systemId, PollId pollId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref DeleteCalls);
            return Task.FromResult(DeleteResult);
        }

        public Task RemoveAlterFromPollsAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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
