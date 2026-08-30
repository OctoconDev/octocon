using Interfold.Bootstrapper.Phases;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class CassandraImagePhaseTests
{
    [Test]
    public async Task BuildDockerBuildArgsUsesStdinDockerfileAndLocalTag()
    {
        var args = CassandraImagePhase.BuildDockerBuildArgs("/tmp/ctx");
        await Assert.That(args).IsEquivalentTo(
            ["build", "-t", CassandraImagePhase.LocalImageTag, "-f", "-", "/tmp/ctx"]);
    }

    [Test]
    public async Task EmbeddedCassandraDockerfileOpensFromSupportResources()
    {
        using var stream = EmbeddedSupportFiles.Open(EmbeddedSupportFiles.CassandraDockerfileRelative);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();
        await Assert.That(text).Contains("FROM cassandra:5");
    }
}
