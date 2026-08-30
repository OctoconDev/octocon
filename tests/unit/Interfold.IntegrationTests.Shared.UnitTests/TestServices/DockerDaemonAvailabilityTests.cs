using Interfold.IntegrationTests.Shared.TestServices;

namespace Interfold.IntegrationTests.Shared.UnitTests.TestServices;

public sealed class DockerDaemonAvailabilityTests
{
    [Test]
    [Arguments("failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine; check if the path is correct and if the daemon is running: open //./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified.", true)]
    [Arguments("Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?", true)]
    [Arguments("error during connect: Get \"http://%2F%2F.%2Fpipe%2FdockerDesktopLinuxEngine/v1.45/info\": open //./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified.", true)]
    [Arguments("[host-aio] raising current=65536 to 166562 for 1 Scylla node(s)\nsh: can't create /proc/sys/fs/aio-max-nr: Permission denied", false)]
    [Arguments("", false)]
    public async Task LooksUnavailable_ClassifiesKnownDaemonErrors(string text, bool expected)
    {
        await Assert.That(DockerDaemonAvailability.LooksUnavailable(text)).IsEqualTo(expected);
    }
}
