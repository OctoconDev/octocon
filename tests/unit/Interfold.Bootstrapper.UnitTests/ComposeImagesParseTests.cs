using Interfold.Bootstrapper.Phases;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Unit tests for <see cref="UpdateImagesPhase.ParseComposePsJson"/>,
/// <see cref="UpdateImagesPhase.ParseContainerImageFields"/>, and
/// <see cref="UpdateImagesPhase.ResolveDesiredDigest"/>.
/// </summary>
public sealed class ComposeImagesParseTests
{
    [Test]
    public async Task ParsesPsJsonArrayShape()
    {
        const string payload = """
        [
            {"ID":"aaa111","Image":"sha256:img1","Service":"msg-db","Name":"deploy-msg-db-1"},
            {"ID":"bbb222","Image":"sha256:img2","Service":"scylla","Name":"deploy-scylla-1"}
        ]
        """;

        var parsed = UpdateImagesPhase.ParseComposePsJson(payload);

        await Assert.That(parsed.Count).IsEqualTo(2);
        await Assert.That(parsed.Single(r => r.Service == "msg-db").ContainerId).IsEqualTo("aaa111");
        await Assert.That(parsed.Single(r => r.Service == "scylla").ContainerId).IsEqualTo("bbb222");
    }

    [Test]
    public async Task ParsesPsJsonLinesShape()
    {
        const string payload = """
        {"ID":"aaa111","Image":"postgres:16","Service":"msg-db","Name":"deploy-msg-db-1"}
        {"ID":"bbb222","Image":"scylla:2026.1","Service":"scylla","Name":"deploy-scylla-1"}
        {"ID":"ccc333","Image":"api:latest","Service":"interfold-api","Name":"deploy-interfold-api-1"}
        """;

        var parsed = UpdateImagesPhase.ParseComposePsJson(payload);

        await Assert.That(parsed.Count).IsEqualTo(3);
        await Assert.That(parsed.Single(r => r.Service == "interfold-api").ContainerId).IsEqualTo("ccc333");
    }

    [Test]
    public async Task EmptyPsInputReturnsEmptyList()
    {
        var parsed = UpdateImagesPhase.ParseComposePsJson("");
        await Assert.That(parsed.Count).IsEqualTo(0);
    }

    [Test]
    public async Task WhitespacePsInputReturnsEmptyList()
    {
        var parsed = UpdateImagesPhase.ParseComposePsJson("   \n  \n  ");
        await Assert.That(parsed.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MalformedPsJsonLineIsSkipped()
    {
        const string payload = """
        {"ID":"aaa","Image":"sha256:a","Service":"msg-db"}
        not json at all
        {"ID":"bbb","Image":"sha256:b","Service":"scylla"}
        """;

        var parsed = UpdateImagesPhase.ParseComposePsJson(payload);

        await Assert.That(parsed.Count).IsEqualTo(2);
        await Assert.That(parsed.Any(r => r.Service == "msg-db")).IsTrue();
        await Assert.That(parsed.Any(r => r.Service == "scylla")).IsTrue();
    }

    [Test]
    public async Task EntryMissingServiceIsIgnored()
    {
        const string payload = """
        {"ID":"aaa","Image":"sha256:orphan"}
        {"ID":"bbb","Image":"sha256:real","Service":"real-svc"}
        """;

        var parsed = UpdateImagesPhase.ParseComposePsJson(payload);

        await Assert.That(parsed.Count).IsEqualTo(1);
        await Assert.That(parsed[0].Service).IsEqualTo("real-svc");
    }

    [Test]
    public async Task EntryMissingContainerIdKeepsEmptyString()
    {
        const string payload = """
        {"Service":"weird-svc"}
        """;

        var parsed = UpdateImagesPhase.ParseComposePsJson(payload);

        await Assert.That(parsed.Count).IsEqualTo(1);
        await Assert.That(parsed[0].ContainerId).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ParseContainerImageFieldsSplitsTabSeparatedLines()
    {
        const string payload = """
        sha256:run1	postgres:16
        sha256:run2	interfold-cassandra:local
        """;

        var parsed = UpdateImagesPhase.ParseContainerImageFields(payload);

        await Assert.That(parsed.Count).IsEqualTo(2);
        await Assert.That(parsed[0].RunningImageId).IsEqualTo("sha256:run1");
        await Assert.That(parsed[0].ConfigImage).IsEqualTo("postgres:16");
        await Assert.That(parsed[1].ConfigImage).IsEqualTo("interfold-cassandra:local");
    }

    [Test]
    public async Task ParseContainerImageFieldsMissingTabKeepsWholeLineAsRunningId()
    {
        var parsed = UpdateImagesPhase.ParseContainerImageFields("sha256:only");
        await Assert.That(parsed.Count).IsEqualTo(1);
        await Assert.That(parsed[0].RunningImageId).IsEqualTo("sha256:only");
        await Assert.That(parsed[0].ConfigImage).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ResolveDesiredDigestPrefersInspectedTagId()
    {
        // Post-pull: Config.Image tag resolved to a new id even while the container still
        // reports the old running ImageID — that mismatch is what triggers recreate.
        var resolved = UpdateImagesPhase.ResolveDesiredDigest(
            runningImageId: "sha256:old",
            inspectedTagId: "sha256:new");

        await Assert.That(resolved).IsEqualTo("sha256:new");
    }

    [Test]
    public async Task ResolveDesiredDigestFallsBackToRunningWhenTagMissing()
    {
        // Tag inspect failed (image record gone / local-only pruned) — keep the running
        // id so the diff stays well-defined instead of inventing an empty digest.
        var resolved = UpdateImagesPhase.ResolveDesiredDigest(
            runningImageId: "sha256:still-running",
            inspectedTagId: null);

        await Assert.That(resolved).IsEqualTo("sha256:still-running");
    }

    [Test]
    public async Task ResolveDesiredDigestFallsBackWhenInspectedIdEmpty()
    {
        var resolved = UpdateImagesPhase.ResolveDesiredDigest(
            runningImageId: "sha256:running",
            inspectedTagId: "");

        await Assert.That(resolved).IsEqualTo("sha256:running");
    }
}
