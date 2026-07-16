using Interfold.Api.Helpers;

namespace Interfold.Api.UnitTests.Helpers;

/// <summary>
/// Unit tests for <see cref="AvatarServingPolicy.Resolve"/>, the decision-helper that
/// Program.cs consults when deciding whether to mount a secondary static-file middleware
/// for avatars. Each test corresponds to one branch of the
/// <c>AvatarStorageRoot</c> × <c>AvatarPublicBase</c> matrix documented on the helper.
/// </summary>
/// <remarks>
/// Tests that need an existing directory create a temp folder per-test under
/// <see cref="Path.GetTempPath"/> and clean it up in a <c>try/finally</c>. A shared
/// instance directory would race under TUnit's parallel runner; the per-test scope
/// keeps the suite safe to parallelise without an <c>[NotInParallel]</c> tag.
/// </remarks>
public sealed class AvatarServingPolicyTests
{
    [Test]
    public async Task BlankStorageRoot_DoesNotServe()
    {
        var (shouldServe, root, requestPath) = AvatarServingPolicy.Resolve(
            avatarStorageRoot: null,
            avatarPublicBase: null);

        using (Assert.Multiple())
        {
            await Assert.That(shouldServe).IsFalse()
                .Because("With no storage root configured, the API has no directory to serve files from.");
            await Assert.That(root).IsEqualTo(string.Empty)
                .Because("The PhysicalRoot is meaningless when ShouldServe is false; the helper normalises it to empty.");
            await Assert.That(requestPath).IsEqualTo(string.Empty)
                .Because("The RequestPath is meaningless when ShouldServe is false; the helper normalises it to empty.");
        }
    }

    [Test]
    public async Task WhitespaceStorageRoot_DoesNotServe()
    {
        var (shouldServe, _, _) = AvatarServingPolicy.Resolve(
            avatarStorageRoot: "   ",
            avatarPublicBase: null);

        await Assert.That(shouldServe).IsFalse()
            .Because("Whitespace-only roots should be treated identically to blank — same operator intent.");
    }

    [Test]
    public async Task MissingDirectoryOnDisk_DoesNotServe()
    {
        // Pick a path that demonstrably does not exist; using a random Guid keeps the
        // assertion deterministic without depending on platform-specific filesystem
        // quirks like case-insensitivity or hidden directories.
        var nonexistent = Path.Combine(Path.GetTempPath(), $"avatar-policy-missing-{Guid.NewGuid():N}");

        var (shouldServe, _, _) = AvatarServingPolicy.Resolve(
            avatarStorageRoot: nonexistent,
            avatarPublicBase: null);

        await Assert.That(shouldServe).IsFalse()
            .Because("Configured-but-not-mounted storage root should refuse to serve so the operator's missing-volume mistake fails loudly instead of stamping 404s.");
    }

    [Test]
    public async Task AbsoluteHttpsBase_DoesNotServe()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var (shouldServe, _, _) = AvatarServingPolicy.Resolve(
                avatarStorageRoot: tempDir,
                avatarPublicBase: "https://cdn.example.com/avatars");

            await Assert.That(shouldServe).IsFalse()
                .Because("An absolute https:// base means a CDN fronts the bytes; the API must not duplicate the serving surface.");
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Test]
    public async Task AbsoluteHttpBase_DoesNotServe()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var (shouldServe, _, _) = AvatarServingPolicy.Resolve(
                avatarStorageRoot: tempDir,
                avatarPublicBase: "http://cdn.example.com/avatars");

            await Assert.That(shouldServe).IsFalse()
                .Because("An absolute http:// base means a CDN/reverse-proxy fronts the bytes; the helper treats it the same as https://.");
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Test]
    public async Task NonHttpAbsoluteBase_IsTreatedAsPathOnly()
    {
        // An exotic scheme (file://, ftp://, custom://) shouldn't be parsed as a CDN
        // delegation. The helper only special-cases http/https — anything else falls
        // through to the path-only branch. This isn't a real operator configuration,
        // but pinning the behaviour means a future "any-absolute-uri-disables-serving"
        // regression surfaces in tests instead of breaking integrations.
        var tempDir = CreateTempDirectory();
        try
        {
            var (shouldServe, _, requestPath) = AvatarServingPolicy.Resolve(
                avatarStorageRoot: tempDir,
                avatarPublicBase: "ftp://files.example.com/avatars");

            using (Assert.Multiple())
            {
                await Assert.That(shouldServe).IsTrue()
                    .Because("Only http(s) absolute bases disable serving; non-web schemes aren't a CDN delegation contract.");
                await Assert.That(requestPath.StartsWith('/')).IsTrue()
                    .Because("The fall-through path still gets a leading-slash anchor — never silently swallowed.");
            }
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Test]
    public async Task BlankPublicBase_UsesDefaultPublicBase()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var (shouldServe, root, requestPath) = AvatarServingPolicy.Resolve(
                avatarStorageRoot: tempDir,
                avatarPublicBase: null);

            using (Assert.Multiple())
            {
                await Assert.That(shouldServe).IsTrue()
                    .Because("Storage on disk + no CDN base = API is the canonical origin → serve.");
                await Assert.That(root).IsEqualTo(tempDir)
                    .Because("PhysicalRoot must echo the configured storage root verbatim so PhysicalFileProvider sees the exact path.");
                await Assert.That(requestPath).IsEqualTo(AvatarServingPolicy.DefaultPublicBase)
                    .Because("Blank base falls back to /avatars — the same prefix LocalAvatarStorage stamps into avatar_url for newly-stored files.");
            }
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Test]
    public async Task PathOnlyBase_PreservesPath()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var (shouldServe, root, requestPath) = AvatarServingPolicy.Resolve(
                avatarStorageRoot: tempDir,
                avatarPublicBase: "/static/avatars");

            using (Assert.Multiple())
            {
                await Assert.That(shouldServe).IsTrue()
                    .Because("Path-only base means the API is still the origin; operator just wants a custom URL prefix.");
                await Assert.That(root).IsEqualTo(tempDir);
                await Assert.That(requestPath).IsEqualTo("/static/avatars")
                    .Because("Caller-supplied path-only base must round-trip verbatim — that's the contract LocalAvatarStorage's URL stamping relies on.");
            }
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Test]
    public async Task PathOnlyBaseMissingLeadingSlash_GetsLeadingSlash()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var (_, _, requestPath) = AvatarServingPolicy.Resolve(
                avatarStorageRoot: tempDir,
                avatarPublicBase: "static/avatars");

            await Assert.That(requestPath).IsEqualTo("/static/avatars")
                .Because("StaticFileOptions.RequestPath requires a leading slash; the helper normalises operator input.");
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Test]
    public async Task PathOnlyBaseWithTrailingSlash_TrimsTrailingSlash()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var (_, _, requestPath) = AvatarServingPolicy.Resolve(
                avatarStorageRoot: tempDir,
                avatarPublicBase: "/avatars/");

            await Assert.That(requestPath).IsEqualTo("/avatars")
                .Because("StaticFileOptions.RequestPath compares trailing-slash-sensitively; trim once at the policy edge so callers don't have to.");
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Test]
    public async Task RootSlashBase_FallsBackToDefault()
    {
        // After trimming the trailing slash a literal "/" collapses to the empty
        // string, which StaticFileOptions.RequestPath would treat as "the static-file
        // middleware short-circuits every request". That would steal /api/* and every
        // SPA route. Pin the fallback so a future "let operators set / as the base"
        // change has to consciously remove this guard.
        var tempDir = CreateTempDirectory();
        try
        {
            var (shouldServe, _, requestPath) = AvatarServingPolicy.Resolve(
                avatarStorageRoot: tempDir,
                avatarPublicBase: "/");

            using (Assert.Multiple())
            {
                await Assert.That(shouldServe).IsTrue();
                await Assert.That(requestPath).IsEqualTo(AvatarServingPolicy.DefaultPublicBase)
                    .Because("A degenerate '/' base must not collapse to an empty prefix — the helper re-anchors to /avatars.");
            }
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Test]
    public async Task AbsoluteHttpLocalhostBase_ServesWithExtractedPath()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var (shouldServe, root, requestPath) = AvatarServingPolicy.Resolve(
                avatarStorageRoot: tempDir,
                avatarPublicBase: "http://localhost/avatars-itest/abc123");

            using (Assert.Multiple())
            {
                await Assert.That(shouldServe).IsTrue()
                    .Because("localhost absolute bases are same-origin — the API still serves bytes in dev/integration.");
                await Assert.That(root).IsEqualTo(tempDir);
                await Assert.That(requestPath).IsEqualTo("/avatars-itest/abc123")
                    .Because("RequestPath must be the URI path so Program.cs middleware matches avatar_url GETs.");
            }
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Test]
    public async Task CustomDefaultPublicBase_IsHonoured()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var (_, _, requestPath) = AvatarServingPolicy.Resolve(
                avatarStorageRoot: tempDir,
                avatarPublicBase: null,
                defaultPublicBase: "/cdn/avatars");

            await Assert.That(requestPath).IsEqualTo("/cdn/avatars")
                .Because("Callers (e.g. unit tests, future feature flags) can override the fallback; the helper honours it instead of hardcoding /avatars.");
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"avatar-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
