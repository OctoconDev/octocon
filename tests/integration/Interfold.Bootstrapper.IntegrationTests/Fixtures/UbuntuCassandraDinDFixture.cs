namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// Ubuntu 24.04 DinD variant that additionally pre-pulls the <c>cassandra:5</c> base image
/// (referenced by <c>FROM cassandra:5</c> in <c>db/cassandra/Dockerfile</c>) so the
/// <see cref="Interfold.Bootstrapper.Phases.CassandraImagePhase.EnsureBuiltAsync"/> call
/// invoked from bootstrap / launch / update-images can build
/// <c>interfold-cassandra:local</c> against the local cache instead of fetching cassandra:5
/// over the network on every test run.
/// </summary>
/// <remarks>
/// A separate fixture (instead of unconditionally adding <c>cassandra:5</c> to
/// <see cref="UbuntuDinDFixture"/>) so the vast majority of tests that only exercise the
/// Scylla path don't pay the extra image pull. The scylla-mode <see cref="UbuntuDinDFixture"/>
/// stays byte-for-byte the same as before; cassandra-mode tests opt in by declaring this
/// fixture in their <c>ClassDataSource</c> attribute.
/// <para>
/// Intentionally keeps the default vfs store (does <b>not</b> enable
/// <see cref="DinDFixtureBase.UseContainerdSnapshotter"/>): nested DinD on GHA cannot
/// mount containerd-overlayfs during <c>docker build</c> of the Cassandra Dockerfile
/// (<c>invalid argument</c> on overlay-on-overlay). The compose#14014 dangling-image
/// surface is covered by unit coverage of the inspect-based digest path plus the
/// regression that still force-rebuilds the local tag under vfs.
/// </para>
/// </remarks>
public sealed class UbuntuCassandraDinDFixture : DinDFixtureBase
{
    protected override string DockerfileName => "Dockerfile.ubuntu-dind";

    protected override IReadOnlyList<string> AdditionalPreloadImages => ["cassandra:5"];
}
