using Interfold.Bootstrapper.Phases;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Unit tests for <see cref="UpdateImagesPhase.ParseComposeImagesJson"/>. Docker Compose v2
/// emits <c>docker compose images --format json</c> as either a single JSON array (older
/// versions) or JSON Lines (2.20+); the parser must accept both without regressing.
/// </summary>
public sealed class ComposeImagesParseTests
{
    [Test]
    public async Task ParsesJsonArrayShape()
    {
        // Older compose plugin (< 2.20) wraps the entries in a top-level array.
        const string payload = """
        [
            {"ID":"sha256:aaa111","Repository":"postgres","Tag":"16","Service":"msg-db","ContainerName":"deploy-msg-db-1"},
            {"ID":"sha256:bbb222","Repository":"scylladb/scylla","Tag":"2026.1","Service":"scylla","ContainerName":"deploy-scylla-1"}
        ]
        """;

        var parsed = UpdateImagesPhase.ParseComposeImagesJson(payload);

        await Assert.That(parsed.Count).IsEqualTo(2);
        await Assert.That(parsed["msg-db"]).IsEqualTo("sha256:aaa111");
        await Assert.That(parsed["scylla"]).IsEqualTo("sha256:bbb222");
    }

    [Test]
    public async Task ParsesJsonLinesShape()
    {
        // Modern compose plugin (2.20+) streams one JSON object per line.
        const string payload = """
        {"ID":"sha256:aaa111","Repository":"postgres","Tag":"16","Service":"msg-db","ContainerName":"deploy-msg-db-1"}
        {"ID":"sha256:bbb222","Repository":"scylladb/scylla","Tag":"2026.1","Service":"scylla","ContainerName":"deploy-scylla-1"}
        {"ID":"sha256:ccc333","Repository":"ghcr.io/azyyyyyy/interfold-api","Tag":"latest","Service":"interfold-api","ContainerName":"deploy-interfold-api-1"}
        """;

        var parsed = UpdateImagesPhase.ParseComposeImagesJson(payload);

        await Assert.That(parsed.Count).IsEqualTo(3);
        await Assert.That(parsed["msg-db"]).IsEqualTo("sha256:aaa111");
        await Assert.That(parsed["scylla"]).IsEqualTo("sha256:bbb222");
        await Assert.That(parsed["interfold-api"]).IsEqualTo("sha256:ccc333");
    }

    [Test]
    public async Task EmptyInputReturnsEmptyMap()
    {
        // An empty stack (no compose file, or compose file with no services) is a valid
        // shape. The parser must return an empty dict rather than throw.
        var parsed = UpdateImagesPhase.ParseComposeImagesJson("");
        await Assert.That(parsed.Count).IsEqualTo(0);
    }

    [Test]
    public async Task WhitespaceInputReturnsEmptyMap()
    {
        var parsed = UpdateImagesPhase.ParseComposeImagesJson("   \n  \n  ");
        await Assert.That(parsed.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MalformedJsonLineIsSkipped()
    {
        // Some older compose plugin versions occasionally interleave stderr with stdout;
        // the parser tolerates a malformed line by skipping it rather than failing the
        // whole diff. Well-formed lines around the bad one must still be captured.
        const string payload = """
        {"ID":"sha256:aaa","Service":"msg-db"}
        not json at all
        {"ID":"sha256:bbb","Service":"scylla"}
        """;

        var parsed = UpdateImagesPhase.ParseComposeImagesJson(payload);

        await Assert.That(parsed.Count).IsEqualTo(2);
        await Assert.That(parsed["msg-db"]).IsEqualTo("sha256:aaa");
        await Assert.That(parsed["scylla"]).IsEqualTo("sha256:bbb");
    }

    [Test]
    public async Task EntryMissingServiceIsIgnored()
    {
        // Defensive: a container without a Service field probably means the compose
        // file is malformed. We don't have a use for such entries in the digest diff,
        // so silently drop them.
        const string payload = """
        {"ID":"sha256:aaa","Repository":"orphan","Tag":"latest"}
        {"ID":"sha256:bbb","Service":"real-svc"}
        """;

        var parsed = UpdateImagesPhase.ParseComposeImagesJson(payload);

        await Assert.That(parsed.Count).IsEqualTo(1);
        await Assert.That(parsed.ContainsKey("real-svc")).IsTrue();
    }

    [Test]
    public async Task EntryMissingIdKeepsEmptyString()
    {
        // A compose plugin that emits {Service:"foo"} without an ID (edge case on some
        // arm64 rebuilds) still lands in the map — but with an empty ID, which the diff
        // treats as "changed vs any real ID". Better than dropping the service silently.
        const string payload = """
        {"Service":"weird-svc"}
        """;

        var parsed = UpdateImagesPhase.ParseComposeImagesJson(payload);

        await Assert.That(parsed.Count).IsEqualTo(1);
        await Assert.That(parsed["weird-svc"]).IsEqualTo(string.Empty);
    }
}
