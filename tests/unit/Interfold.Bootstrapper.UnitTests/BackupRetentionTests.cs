using Interfold.Bootstrapper.Phases;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Pure-logic tests for <see cref="BackupRetention.Prune"/>. The function takes an unordered
/// set of <see cref="FileInfo"/>s + a retain count and returns the files to delete (oldest by
/// mtime, oldest-first). Every test below stages files on disk in a temp directory rather
/// than mocking FileInfo, since FileInfo's mtime fields are populated from the live
/// filesystem and there's no public seam to fake them.
/// </summary>
public sealed class BackupRetentionTests
{
    /// <summary>
    /// Spins up a fresh temp dir, writes <paramref name="count"/> placeholder files with
    /// distinct, monotonic LastWriteTimeUtc stamps (oldest first), and returns them in the
    /// order created. Callers can shuffle if they need to exercise the orderer.
    /// </summary>
    private static List<FileInfo> StageFiles(string dir, int count, string ext = ".dump")
    {
        var baseTime = DateTime.UtcNow.AddDays(-30);
        var files = new List<FileInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(dir, $"file-{i:D4}{ext}");
            File.WriteAllText(path, $"content-{i}");
            // Spread the mtimes by one hour so the ordering is unambiguous regardless of
            // OS-level clock resolution (NTFS is 100ns, ext4 is ns, but APFS is 1ns —
            // a 1-hour spacing wipes out any platform-specific quirks).
            File.SetLastWriteTimeUtc(path, baseTime.AddHours(i));
            files.Add(new FileInfo(path));
        }
        return files;
    }

    [Test]
    public async Task EmptyInputReturnsEmpty()
    {
        // No files means nothing to prune, regardless of retain count. Important for the
        // first-ever backup invocation, where the component subdirectory is empty.
        var result = BackupRetention.Prune([], keep: 14).ToList();
        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task FewerFilesThanRetainKeepsAll()
    {
        // 5 files, retain 14 → prune nothing. The "ramp-up" period for a fresh deployment.
        using var scratch = TestSupport.NewScratchDir("interfold-retention");
        var files = StageFiles(scratch.Path, 5);
        var result = BackupRetention.Prune(files, keep: 14).ToList();
        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task ExactlyRetainKeepsAll()
    {
        // Boundary case: exactly retainCount files. The "==" boundary should not delete
        // anything — only "more than retain" triggers deletion.
        using var scratch = TestSupport.NewScratchDir("interfold-retention");
        var files = StageFiles(scratch.Path, 14);
        var result = BackupRetention.Prune(files, keep: 14).ToList();
        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task OneOverRetainDeletesOldest()
    {
        // 15 files, retain 14 → delete the single oldest. Steady-state daily backup behaviour.
        using var scratch = TestSupport.NewScratchDir("interfold-retention");
        var files = StageFiles(scratch.Path, 15);
        var result = BackupRetention.Prune(files, keep: 14).ToList();
        await Assert.That(result.Count).IsEqualTo(1);
        // file-0000 has the oldest mtime per StageFiles.
        await Assert.That(result[0].Name).IsEqualTo("file-0000.dump");
    }

    [Test]
    public async Task ManyOverRetainDeletesOldestN()
    {
        // 100 files, retain 14 → delete the 86 oldest. Caps the worst-case "operator ran
        // backup hourly for a week before turning on retention" cleanup.
        using var scratch = TestSupport.NewScratchDir("interfold-retention");
        var files = StageFiles(scratch.Path, 100);
        var result = BackupRetention.Prune(files, keep: 14).ToList();
        await Assert.That(result.Count).IsEqualTo(86);
        // The first 86 (oldest-first) must be the ones the helper returns.
        for (var i = 0; i < 86; i++)
        {
            await Assert.That(result[i].Name).IsEqualTo($"file-{i:D4}.dump");
        }
    }

    [Test]
    public async Task ShuffledInputStillOrdersByMtime()
    {
        // The helper must internally sort — the caller hands us a DirectoryInfo enumeration
        // whose order is filesystem-defined (NTFS is alphabetical, ext4 is hash-ordered) and
        // bears no relationship to mtime. We shuffle the staged list before passing it in to
        // catch any implementation that just .Take()s without sorting.
        using var scratch = TestSupport.NewScratchDir("interfold-retention");
        var files = StageFiles(scratch.Path, 20);
        var shuffled = files.OrderBy(_ => Guid.NewGuid()).ToList();
        var result = BackupRetention.Prune(shuffled, keep: 5).ToList();
        await Assert.That(result.Count).IsEqualTo(15);
        for (var i = 0; i < 15; i++)
        {
            await Assert.That(result[i].Name).IsEqualTo($"file-{i:D4}.dump");
        }
    }

    [Test]
    public async Task KeepZeroDeletesEverything()
    {
        // keep=0 is the "stop retaining" semantics; pruning would wipe the directory clean.
        // The validator rejects 0 from operator-supplied config (RetainCount: 1..1000), but
        // the pure helper still accepts it — defence in depth lets callers reuse Prune in
        // future "clean up before resnapshot" flows without re-validating.
        using var scratch = TestSupport.NewScratchDir("interfold-retention");
        var files = StageFiles(scratch.Path, 3);
        var result = BackupRetention.Prune(files, keep: 0).ToList();
        await Assert.That(result.Count).IsEqualTo(3);
    }

    [Test]
    public async Task NegativeKeepThrows()
    {
        // Negative retain count is a programming error; surface it as ArgumentOutOfRangeException
        // rather than silently treating it as "delete everything" or "keep everything".
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => BackupRetention.Prune([], keep: -1).ToList());
        await Assert.That(ex.ParamName).IsEqualTo("keep");
    }

    [Test]
    public async Task NullFilesThrows()
    {
        // Explicit null guard surfaces the misuse instead of throwing NullReferenceException
        // inside the enumerator. Defensive but cheap.
        var ex = Assert.Throws<ArgumentNullException>(
            () => BackupRetention.Prune(null!, keep: 1).ToList());
        await Assert.That(ex.ParamName).IsEqualTo("files");
    }
}
