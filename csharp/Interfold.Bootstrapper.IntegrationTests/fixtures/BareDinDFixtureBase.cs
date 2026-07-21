namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// Base class for DinD fixtures rooted on stripped distro images that ship without docker
/// or openssl. Collapses the byte-identical <c>InitializeAsync</c> that Ubuntu and Fedora
/// bare-prereqs fixtures used to hold verbatim, and exposes two hooks
/// (<see cref="DistroLabel"/>, <see cref="PackageManager"/>) so the diagnostic message
/// stays specific per subclass.
/// </summary>
/// <remarks>
/// <para>
/// Concrete subclasses stay sealed — TUnit's <c>[ClassDataSource]</c> needs a concrete
/// non-abstract type per test class, so an abstract base is fine but the leaf must not be.
/// </para>
/// <para>
/// The one-shot precondition guard sits here (rather than inside individual
/// <c>PrereqsPhaseTests</c> cases) because three of the four tests that share this
/// fixture run <c>bootstrap --fault-inject=after-prereqs</c>, which installs docker as a
/// side effect. Any per-test "docker isn't installed yet" assertion would only survive
/// for whichever test happened to run first. Lifting it to fixture-build time turns it
/// into a one-shot assertion against the fixture IMAGE, which is what the original test
/// comment described it as.
/// </para>
/// </remarks>
public abstract class BareDinDFixtureBase : DinDFixtureBase
{
    /// <summary>
    /// Human-readable distro name for the "expects a bare X image" error message.
    /// Example values: <c>"Ubuntu"</c>, <c>"Fedora"</c>.
    /// </summary>
    protected abstract string DistroLabel { get; }

    /// <summary>
    /// The package manager name whose install path this fixture exists to exercise
    /// (referenced in the guard's error message). Example values: <c>"apt"</c>, <c>"dnf"</c>.
    /// </summary>
    protected abstract string PackageManager { get; }

    // No dockerd to start, no API image to load — skip the parent's PreloadImages flow.
    // Bare fixtures ship without dockerd, so `docker load` would fail with connection
    // refused anyway.
    protected override bool PreloadImages => false;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync().ConfigureAwait(false);
        var prePath = await ExecAsync(["sh", "-c", "command -v docker || true"]).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(prePath.Stdout))
        {
            throw new InvalidOperationException(
                $"{GetType().Name} expects a bare {DistroLabel} image without docker " +
                $"pre-installed, but `command -v docker` returned '{prePath.Stdout.Trim()}'. " +
                $"Inspect {DockerfileName} — the {PackageManager} install path can no longer be exercised.");
        }
    }
}
