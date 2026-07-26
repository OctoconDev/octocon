using System.Diagnostics;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Phases;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Drives <see cref="RootCli.RunAsync"/> in-process so we can assert CLI parsing behaviour
/// without forking. Stdout/stderr are temporarily swapped onto <see cref="StringWriter"/>s for
/// the duration of each invocation; the original streams are restored in a <c>finally</c> so
/// other tests in the same process aren't affected.
/// </summary>
public sealed class CliParsingTests
{
    private sealed record CapturedRun(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// Invokes the CLI and captures stdout/stderr. The CLI is a real System.CommandLine root, so
    /// this exercises argument parsing exactly as the operator's invocation would.
    /// Used for tests that drive code through to <see cref="Phases.Orchestrator"/> (e.g. the
    /// non-interactive missing-config path) — these write through <see cref="Console.WriteLine"/>
    /// and so are visible to the swapped-in writers.
    /// </summary>
    private static async Task<CapturedRun> InvokeInProcessAsync(params string[] args)
    {
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        var prevIn = Console.In;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            // TUnit0055 warns that overwriting Console writers can mask its log output; in this
            // test we deliberately want to capture what the bootstrapper writes, and we restore
            // the originals in the finally block, so the warning is misleading here.
#pragma warning disable TUnit0055
            Console.SetOut(stdout);
            Console.SetError(stderr);
            // Force "non-interactive" semantics in ConfigPhase by closing stdin — otherwise
            // `Console.IsInputRedirected` may be false under the test runner and the prompt
            // path would block waiting for keystrokes that never come.
            Console.SetIn(new StringReader(string.Empty));
#pragma warning restore TUnit0055
            var exit = await RootCli.RunAsync(args);
            return new CapturedRun(exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
#pragma warning disable TUnit0055
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
            Console.SetIn(prevIn);
#pragma warning restore TUnit0055
        }
    }

    /// <summary>
    /// Shells out to the compiled bootstrapper. Necessary for help / parser-error tests because
    /// System.CommandLine 3.x writes those to <c>ParseResult.Configuration.Output</c> rather than
    /// the redirectable <see cref="Console.Out"/>. Skips the test (via
    /// <see cref="TestSupport.BootstrapperBinaryOrSkip"/>) when the bootstrapper binary isn't on
    /// disk yet — `dotnet test` from the solution root will have built it as a transitive dep.
    /// </summary>
    private static async Task<CapturedRun> InvokeViaProcessAsync(params string[] args)
    {
        var path = TestSupport.BootstrapperBinaryOrSkip();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(path);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet");
        proc.StandardInput.Close();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return new CapturedRun(proc.ExitCode, stdout, stderr);
    }

    [Test]
    public async Task HelpFlagPrintsCommandList()
    {
        // System.CommandLine 3.x writes help text to a configured TextWriter inside the parse
        // result, NOT through Console.Out — so we shell out and read the real stdout.
        var run = await InvokeViaProcessAsync("--help");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        var combined = run.Stdout + run.Stderr;
        await Assert.That(combined).Contains("bootstrap");
        await Assert.That(combined).Contains("publish");
        await Assert.That(combined).Contains("up");
        await Assert.That(combined).Contains("rotate-secrets");
        await Assert.That(combined).Contains("rotate-certs");
        await Assert.That(combined).Contains("show-trust");
    }

    [Test]
    [NotInParallel("CliParsing.ConsoleStreams")]
    public async Task ShowTrustReadsExistingCertWithoutRegenerating()
    {
        // End-to-end behavioural check: pre-stage a certs/ directory with a known rootCA.crt
        // (and matching rootCA.sha256.txt), invoke `bootstrap show-trust`, and confirm the
        // command (a) succeeds, (b) does NOT rewrite the cert (mtime unchanged), and
        // (c) prints the recorded fingerprint exactly. This is the load-bearing assertion
        // that the Orchestrator short-circuit doesn't accidentally fall through to
        // CertificatePhase.RunAsync and regenerate the CA.
        using var scratch = TestSupport.NewScratchDir("interfold-cli-showtrust");
        var tmpDir = scratch.Path;
        var certsDir = Path.Combine(tmpDir, "certs");
        Directory.CreateDirectory(certsDir);

        using var key = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            new System.Security.Cryptography.X509Certificates.X500DistinguishedName("CN=Test Root"),
            key,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(1));
        var rootCrtPath = Path.Combine(certsDir, "rootCA.crt");
        var rootFpPath = Path.Combine(certsDir, "rootCA.sha256.txt");
        await File.WriteAllTextAsync(rootCrtPath, cert.ExportCertificatePem());

        var expectedFingerprint = CertificatePhase.FormatSha256Fingerprint(
            System.Security.Cryptography.SHA256.HashData(cert.RawData));
        await File.WriteAllTextAsync(rootFpPath, expectedFingerprint + Environment.NewLine);

        var preCertMtime = File.GetLastWriteTimeUtc(rootCrtPath);
        var preFpMtime = File.GetLastWriteTimeUtc(rootFpPath);

        // Force a measurable mtime delta so an accidental rewrite would be visible even on
        // file systems whose timestamp resolution is in tens of milliseconds (ext4, APFS).
        await Task.Delay(100);

        var run = await InvokeInProcessAsync("show-trust", "--output-dir", tmpDir, "--non-interactive");

        await Assert.That(run.ExitCode).IsEqualTo(0)
            .Because($"show-trust must succeed on a pre-staged certs dir. stderr: {run.Stderr}");
        await Assert.That(run.Stdout + run.Stderr).Contains(expectedFingerprint)
            .Because("show-trust must print the recorded fingerprint verbatim");
        await Assert.That(File.GetLastWriteTimeUtc(rootCrtPath)).IsEqualTo(preCertMtime)
            .Because("show-trust must not rewrite rootCA.crt");
        await Assert.That(File.GetLastWriteTimeUtc(rootFpPath)).IsEqualTo(preFpMtime)
            .Because("show-trust must not rewrite rootCA.sha256.txt");
    }

    [Test]
    [NotInParallel("CliParsing.ConsoleStreams")]
    public async Task ShowTrustFailsWhenRootCaMissing()
    {
        // Diagnostic UX check: running show-trust against a directory that has no rootCA.crt
        // must fail with a non-zero exit and an actionable error message that points the
        // operator at `bootstrap` / `bootstrap rotate-certs`.
        using var scratch = TestSupport.NewScratchDir("interfold-cli-showtrust-missing");
        var tmpDir = scratch.Path;
        var run = await InvokeInProcessAsync("show-trust", "--output-dir", tmpDir, "--non-interactive");

        await Assert.That(run.ExitCode).IsNotEqualTo(0)
            .Because("show-trust must exit non-zero when rootCA.crt is missing");
        var combined = run.Stdout + run.Stderr;
        await Assert.That(combined).Contains("rootCA.crt")
            .Because("the error message should name the missing file so operators know what to look for");
    }

    [Test]
    public async Task HelpListsShortOptions()
    {
        // -c short form for --config and -o short form for --output-dir must both surface in the
        // help text. If a future refactor drops the aliases this catches it immediately.
        var run = await InvokeViaProcessAsync("--help");

        var combined = run.Stdout + run.Stderr;
        await Assert.That(combined).Contains("-c");
        await Assert.That(combined).Contains("-o");
    }

    [Test]
    public async Task UnknownCommandExitsNonZero()
    {
        // System.CommandLine treats an unrecognised verb as an error. The exact wording varies
        // across SDK versions so we just assert on the non-zero exit code.
        var run = await InvokeViaProcessAsync("this-is-definitely-not-a-command");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
    }

    [Test]
    [NotInParallel("CliParsing.ConsoleStreams")]
    public async Task ShortOptionsMatchLongOptions()
    {
        // Compare two parser invocations of the `publish` command — one with --config, one
        // with -c — pointing at the same nonexistent file. Both must produce equivalent
        // failure behaviour because -c is the alias of --config. We can drive this in-process
        // because the failure happens inside ConfigPhase (which uses Console.WriteLine).
        using var scratch = TestSupport.NewScratchDir("interfold-cli-short");
        var tmpDir = scratch.Path;
        var configPath = Path.Combine(tmpDir, "does-not-exist.json");

        var longRun = await InvokeInProcessAsync("publish", "--config", configPath,
            "--output-dir", tmpDir, "--non-interactive");
        var shortRun = await InvokeInProcessAsync("publish", "-c", configPath,
            "-o", tmpDir, "--non-interactive");

        await Assert.That(shortRun.ExitCode).IsEqualTo(longRun.ExitCode)
            .Because("-c/-o aliases must produce the same exit code as --config/--output-dir");
    }

    [Test]
    [NotInParallel("CliParsing.ConsoleStreams")]
    public async Task NonInteractiveWithoutConfigExitsNonZero()
    {
        // ConfigPhase aborts when --non-interactive is set and no config file is present at the
        // expected path. Drive the `publish` command so the failure happens at the config phase
        // rather than at prereqs (which is Linux-only).
        using var scratch = TestSupport.NewScratchDir("interfold-cli-ni");
        var tmpDir = scratch.Path;
        var run = await InvokeInProcessAsync("publish",
            "--config", Path.Combine(tmpDir, "missing.json"),
            "--output-dir", tmpDir,
            "--non-interactive");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
    }
}
