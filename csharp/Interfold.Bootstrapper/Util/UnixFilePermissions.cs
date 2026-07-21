using System.Runtime.InteropServices;
using Interfold.Bootstrapper.Cli;

namespace Interfold.Bootstrapper.Util;

/// <summary>
/// Cross-platform wrapper around <c>libc chmod(2)</c> for the "restrict / relax file
/// permissions after writing" pattern the certificate + secrets phases both need.
/// No-op on Windows (where ACLs don't translate cleanly to a Unix mode) — we expect
/// Linux for production self-hosting; the Windows path is exercised only by developer
/// workstations and CI runners for round-tripping test fixtures.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes are exposed today, both grep-verified from the two phases that used to
/// hold verbatim copies of this P/Invoke:
/// </para>
/// <list type="bullet">
///   <item><see cref="SetOwnerOnly"/> — 0600, owner read/write only. Used for signing
///     material (rootCA.key) and the secrets store (secrets.json) where a non-owner
///     shell user should not be able to read the contents even if they've traversed
///     into the output directory.</item>
///   <item><see cref="SetWorldReadable"/> — 0644, readable by any UID, writable only by
///     the owner. Used for artifacts bind-mounted into the API container which runs as
///     UID 64198 (non-root) and cannot otherwise read the bootstrapper's default 0600
///     files. The API container never mutates these files so read-only is sufficient.</item>
/// </list>
/// <para>
/// Failures are non-fatal — logged as warnings rather than thrown — because a wrong-
/// permissions file is still functional (just less secure or wrongly-scoped for the
/// downstream mount). The phase completing lets the operator inspect and fix manually.
/// The alternative (fail-fast) would leave the deployment in a "half-generated" state
/// that's harder to recover from than the current permissive-but-warned state.
/// </para>
/// </remarks>
internal static partial class UnixFilePermissions
{
    private const int S_600 = 0x180; // 0o600 - owner read/write only
    private const int S_644 = 0x1A4; // 0o644 - owner read/write, group/other read

    /// <summary>
    /// Applies <c>0600</c> (owner read/write only). Windows: no-op. Failures logged
    /// as a warning; the caller continues.
    /// </summary>
    /// <param name="path">The file path to chmod.</param>
    /// <param name="logger">Where the warning goes when the chmod call returns non-zero.</param>
    /// <param name="artifactLabel">Human-readable label for the file kind used in the warning
    /// message (e.g. <c>"key file"</c>, <c>"file"</c>). Defaults to <c>"file"</c>.</param>
    public static void SetOwnerOnly(string path, PhaseLogger logger, string artifactLabel = "file")
        => Apply(path, S_600, "0600", logger, artifactLabel);

    /// <summary>
    /// Applies <c>0644</c> (owner read/write, group + other read). Windows: no-op.
    /// Failures logged as a warning; the caller continues.
    /// </summary>
    /// <param name="path">The file path to chmod.</param>
    /// <param name="logger">Where the warning goes when the chmod call returns non-zero.</param>
    /// <param name="artifactLabel">Human-readable label for the file kind used in the warning
    /// message (e.g. <c>"cert file"</c>, <c>"file"</c>). Defaults to <c>"file"</c>.</param>
    public static void SetWorldReadable(string path, PhaseLogger logger, string artifactLabel = "file")
        => Apply(path, S_644, "0644", logger, artifactLabel);

    private static void Apply(string path, int mode, string modeDisplay, PhaseLogger logger, string artifactLabel)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }

        var rc = NativeMethods.chmod(path, mode);
        if (rc != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            logger.Warn($"chmod({path}, {modeDisplay}) failed: errno={err} ({artifactLabel} written but permissions not adjusted)");
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial int chmod(string path, int mode);
    }
}
