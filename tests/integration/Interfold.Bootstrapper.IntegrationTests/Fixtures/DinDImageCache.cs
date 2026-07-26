using System.Collections.Concurrent;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;

namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// Caches built DinD images by Dockerfile filename so all subclasses of a given fixture share
/// the build cost. The cache is keyed by the on-disk Dockerfile path; the value is the resolved
/// image reference Testcontainers hands back from <c>ImageFromDockerfileBuilder.Build()</c>.
/// </summary>
internal static class DinDImageCache
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<IFutureDockerImage>>> Cache = new();

    public static Task<IFutureDockerImage> GetOrBuildAsync(string dockerfileName, string fixturesDir)
    {
        var entry = Cache.GetOrAdd(
            dockerfileName,
            key => new Lazy<Task<IFutureDockerImage>>(() => BuildAsync(key, fixturesDir)));
        return entry.Value;
    }

    private static async Task<IFutureDockerImage> BuildAsync(string dockerfileName, string fixturesDir)
    {
        var image = new ImageFromDockerfileBuilder()
            .WithName($"interfold-bootstrapper-test/{dockerfileName.ToLowerInvariant()}:latest")
            .WithDockerfileDirectory(fixturesDir)
            .WithDockerfile(dockerfileName)
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync().ConfigureAwait(false);
        return image;
    }
}

