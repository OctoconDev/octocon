using Interfold.IntegrationTests.Shared.TestServices;

namespace Interfold.IntegrationTests.Shared.UnitTests.TestServices;

[NotInParallel(nameof(ScyllaRackDcMountPathsTests))]
public sealed class ScyllaRackDcMountPathsTests
{
    private string _scratch = null!;

    [Before(Test)]
    public void ArrangeScratch()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "rackdc-mount-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
        Directory.CreateDirectory(Path.Combine(_scratch, "db", "scylla"));
        foreach (var region in new[] { "nam", "eur", "sam", "sas", "eas", "ocn", "gdpr" })
        {
            File.WriteAllText(
                Path.Combine(_scratch, "db", "scylla", $"cassandra-rackdc.{region}.properties"),
                $"dc={region}\nrack=rack1\n");
        }
    }

    [After(Test)]
    public void CleanupScratch()
    {
        try { Directory.Delete(_scratch, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public void VerifyAllPresent_WhenComplete_DoesNotThrow()
    {
        ScyllaRackDcMountPaths.VerifyAllPresent(_scratch);
    }

    [Test]
    public async Task VerifyAllPresent_WhenRegionMissing_ThrowsWithPath()
    {
        File.Delete(Path.Combine(_scratch, "db", "scylla", "cassandra-rackdc.gdpr.properties"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.Run(() => ScyllaRackDcMountPaths.VerifyAllPresent(_scratch)));

        await Assert.That(ex!.Message).Contains("cassandra-rackdc.gdpr.properties");
        await Assert.That(ex.Message).Contains("GossipingPropertyFileSnitch");
        await Assert.That(ex.Message).Contains("do not fall back to SimpleSnitch");
    }
}
