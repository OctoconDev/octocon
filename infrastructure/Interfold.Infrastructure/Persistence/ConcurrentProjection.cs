namespace Interfold.Infrastructure.Persistence;

public static class ConcurrentProjection
{
    public static async Task<List<TResult>> SelectWithConcurrencyAsync<TSource, TResult>(
        IEnumerable<TSource> source,
        int maxConcurrency,
        Func<TSource, Task<TResult>> selector,
        CancellationToken cancellationToken)
    {
        var concurrency = Math.Max(1, maxConcurrency);
        using var semaphore = new SemaphoreSlim(concurrency);

        var tasks = source.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await selector(item);
            }
            finally
            {
                semaphore.Release();
            }
        });

        return (await Task.WhenAll(tasks)).ToList();
    }
}
