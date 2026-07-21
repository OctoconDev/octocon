namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Test-side scaffold for avatar-storage tests that need a per-test filesystem storage root
/// and per-test <c>OCTOCON_AVATAR_PUBLIC_BASE</c> without leaking either onto the
/// session-shared factory. See the doc-comments on the individual avatar-multipart tests
/// for the full rationale (before this helper, three tests hand-rolled ~17 lines each of
/// runId + storage root + private-factory build + finally-cleanup, and one of them had
/// already caused a ~50-downstream-500 cascade the first time it was written against the
/// shared factory).
///
/// <para>
/// The body runs with a private <see cref="InterfoldWebApplicationFactory"/> whose
/// <c>OCTOCON_AVATAR_STORAGE_ROOT</c> is a fresh temp dir and whose
/// <c>OCTOCON_AVATAR_PUBLIC_BASE</c> matches, so URL-path assertions can pin against the
/// second callback argument (<c>publicBasePath</c>). Both the private factory and the temp
/// dir are torn down on return, whether the body throws or not.
/// </para>
/// </summary>
internal static class IsolatedAvatarStorage
{
    public static async Task RunAsync(
        IWebFactoryFixture fixture,
        Func<HttpClient, string, Task> body)
    {
        var runId = Guid.NewGuid().ToString("N");
        var storageRoot = Path.Combine(Path.GetTempPath(), "octocon-itest", "avatars", runId);
        // AvatarPublicBase carries [AbsoluteHttpUri] validation on StorageConfiguration,
        // so the configured value MUST be an absolute http(s) URL. WebApplicationFactory's
        // default BaseAddress is http://localhost/, so "http://localhost" matches
        // request-origin resolution downstream. publicBasePath is what callers assert
        // against, because UrlPathStartsWith compares Uri.AbsolutePath (path, not full URL).
        var publicBasePath = $"/avatars-itest/{runId}";
        var publicBase = $"http://localhost{publicBasePath}";

        try
        {
            Directory.CreateDirectory(storageRoot);

            await using var isolatedFactory = fixture.CreatePrivateFactory()
                .WithConfiguration("OCTOCON_AVATAR_STORAGE_ROOT", storageRoot)
                .WithConfiguration("OCTOCON_AVATAR_PUBLIC_BASE", publicBase);

            using var client = isolatedFactory.CreateClient();

            await body(client, publicBasePath);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
                Directory.Delete(storageRoot, true);
        }
    }
}
