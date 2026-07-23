using Interfold.Infrastructure.Coordination;

namespace Interfold.IntegrationTests.Services;

public sealed class InProcessEventBusReconnectTests : BaseEndpointTest
{
    [Test]
    public async Task Publish_DoesNotThrow_AfterSubscriberDisconnectAndReconnect()
    {
        using var bus = new InProcessEventBus();

        using var firstSubscriptionCts = new CancellationTokenSource();
        await using var firstEnumerator = bus
            .SubscribeAsync<TestEvent>(firstSubscriptionCts.Token)
            .GetAsyncEnumerator(firstSubscriptionCts.Token);

        await bus.PublishAsync(new TestEvent("first"));
        var firstReceived = await MoveNextWithTimeoutAsync(firstEnumerator, TimeSpan.FromSeconds(2));
        await Assert.That(firstReceived).IsTrue();

        firstSubscriptionCts.Cancel();
        await firstEnumerator.DisposeAsync();

        using var secondSubscriptionCts = new CancellationTokenSource();
        await using var secondEnumerator = bus
            .SubscribeAsync<TestEvent>(secondSubscriptionCts.Token)
            .GetAsyncEnumerator(secondSubscriptionCts.Token);

        await bus.PublishAsync(new TestEvent("second"));
        var secondReceived = await MoveNextWithTimeoutAsync(secondEnumerator, TimeSpan.FromSeconds(2));
        using (Assert.Multiple())
        {
            await Assert.That(secondReceived).IsTrue();
            await Assert.That(secondEnumerator.Current.Value).IsEqualTo("second");
        }
    }

    [Test]
    public async Task Publish_RemainsHealthy_AcrossRepeatedReconnectChurn()
    {
        using var bus = new InProcessEventBus();

        for (var i = 0; i < 25; i++)
        {
            using var subscriptionCts = new CancellationTokenSource();
            await using var enumerator = bus
                .SubscribeAsync<TestEvent>(subscriptionCts.Token)
                .GetAsyncEnumerator(subscriptionCts.Token);

            await bus.PublishAsync(new TestEvent($"cycle-{i}"));
            var received = await MoveNextWithTimeoutAsync(enumerator, TimeSpan.FromSeconds(2));
            await Assert.That(received).IsTrue();

            subscriptionCts.Cancel();
            await enumerator.DisposeAsync();
        }

        using var finalCts = new CancellationTokenSource();
        await using var finalEnumerator = bus
            .SubscribeAsync<TestEvent>(finalCts.Token)
            .GetAsyncEnumerator(finalCts.Token);

        await bus.PublishAsync(new TestEvent("final"), finalCts.Token);
        var finalReceived = await MoveNextWithTimeoutAsync(finalEnumerator, TimeSpan.FromSeconds(2));
        using (Assert.Multiple())
        {
            await Assert.That(finalReceived).IsTrue();
            await Assert.That(finalEnumerator.Current.Value).IsEqualTo("final");
        }
    }

    private static async Task<bool> MoveNextWithTimeoutAsync(IAsyncEnumerator<TestEvent> enumerator, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        var completedTask = await Task.WhenAny(moveNextTask, Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token));

        if (!ReferenceEquals(completedTask, moveNextTask))
        {
            return false;
        }

        return await moveNextTask;
    }

    private sealed record TestEvent(string Value);
}