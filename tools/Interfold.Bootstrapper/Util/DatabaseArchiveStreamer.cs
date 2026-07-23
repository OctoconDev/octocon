using System.IO.Compression;

namespace Interfold.Bootstrapper.Util;

/// <summary>
/// Trust-boundary helpers for backup and restore archive plumbing. Both phases
/// (<c>BackupPhase</c> and <c>RestorePhase</c>) previously hand-rolled a wrapper around
/// <see cref="ProcessStreamRunner"/> that stitched together three concerns:
/// <list type="number">
///   <item>Streaming a running process's stdout to a file, optionally through a gzip encoder.</item>
///   <item>Streaming a source file into a process's stdin, optionally through a gzip decoder.</item>
///   <item>Injecting <c>PGPASSWORD</c> onto the process environment (never on the argv, so it
///         can't surface in <c>ps</c> output on a shared host).</item>
/// </list>
/// Centralising here means the two symmetrical paths (dump-to-disk, restore-from-disk)
/// share one authoritative shape for how the compressed transfer buffer is built and how
/// the admin password is smuggled onto the exec — a future change to either invariant
/// (e.g. bumping gzip level, moving off the process env for password propagation) moves
/// in lockstep instead of drifting between the two phases.
/// </summary>
internal static class DatabaseArchiveStreamer
{
    /// <summary>
    /// The canonical env-var name that Postgres client tools (<c>pg_dump</c>,
    /// <c>pg_restore</c>, <c>psql</c>) consult for the password. Kept as a constant so any
    /// site that needs to build a per-invocation env dict never re-types the string.
    /// </summary>
    public const string PgPasswordEnvVar = "PGPASSWORD";

    /// <summary>
    /// Canonical env dictionary carrying <see cref="PgPasswordEnvVar"/>. The two phases
    /// used to build this dict inline; centralising forces the trust boundary to move
    /// through one factory so a future addition (e.g. <c>PGSSLROOTCERT</c>) can be added
    /// once instead of in both call sites.
    /// </summary>
    public static IDictionary<string, string?> PgPasswordEnv(string password)
        => new Dictionary<string, string?> { [PgPasswordEnvVar] = password };

    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> and streams its
    /// stdout directly into <paramref name="destinationPath"/>. Throws if the process exits
    /// non-zero; partial output is preserved on disk for diagnosis but the caller deletes
    /// it before propagating the exception when it's known to be empty/corrupt.
    /// </summary>
    /// <param name="compress">
    /// When true, the destination file is wrapped in a <see cref="GZipStream"/> so the
    /// process's stdout is written as gzip-compressed bytes on disk. Used by the Scylla
    /// backup path where <c>docker cp</c> emits an uncompressed tar but the target
    /// filename is <c>.tar.gz</c>. The Postgres backup path leaves this false —
    /// <c>pg_dump -Fc</c> already emits a compressed custom-format archive.
    /// </param>
    public static Task StreamProcessStdoutToFileAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IDictionary<string, string?>? environment,
        string destinationPath,
        CancellationToken ct,
        bool compress = false)
        => ProcessStreamRunner.RunAsync(
            fileName, arguments, environment,
            redirectStandardInput: false,
            forwardStdout: false,
            async proc =>
            {
                await using var fs = File.Create(destinationPath);
                if (compress)
                {
                    // CompressionLevel.Fastest keeps CPU well below the docker-cp / disk
                    // throughput ceiling on typical backup data (SSTables compress poorly
                    // anyway because they're already LZ4-compressed internally). Higher
                    // levels would burn CPU for a marginal size win on already-compressed
                    // payload.
                    await using var gz = new GZipStream(fs, CompressionLevel.Fastest, leaveOpen: false);
                    await proc.StandardOutput.BaseStream.CopyToAsync(gz, ct).ConfigureAwait(false);
                }
                else
                {
                    await proc.StandardOutput.BaseStream.CopyToAsync(fs, ct).ConfigureAwait(false);
                }
            },
            ct);

    /// <summary>
    /// Streams the contents of <paramref name="sourcePath"/> into
    /// <paramref name="fileName"/>'s stdin, optionally decompressing with
    /// <see cref="GZipStream"/> on the way (used for the Scylla restore path, whose
    /// on-disk archive is <c>.tar.gz</c> but the target <c>docker cp</c> expects a raw
    /// tar on stdin). Throws on non-zero exit; stderr is included in the exception
    /// message. Deliberately does NOT redirect stdout to disk — restore commands emit
    /// diagnostic output that we want on the operator's terminal.
    /// </summary>
    public static Task StreamFileToProcessStdinAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IDictionary<string, string?>? environment,
        string sourcePath,
        bool decompress,
        CancellationToken ct)
        => ProcessStreamRunner.RunAsync(
            fileName, arguments, environment,
            redirectStandardInput: true,
            forwardStdout: true,
            async proc =>
            {
                await using var src = File.OpenRead(sourcePath);
                if (decompress)
                {
                    await using var gz = new GZipStream(src, CompressionMode.Decompress, leaveOpen: false);
                    await gz.CopyToAsync(proc.StandardInput.BaseStream, ct).ConfigureAwait(false);
                }
                else
                {
                    await src.CopyToAsync(proc.StandardInput.BaseStream, ct).ConfigureAwait(false);
                }
                await proc.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
                proc.StandardInput.Close();
            },
            ct);
}
