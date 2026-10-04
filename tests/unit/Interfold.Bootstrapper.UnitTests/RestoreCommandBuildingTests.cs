using Interfold.Bootstrapper.Phases;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Archive-resolution assertions for <see cref="RestorePhase"/>. Pins the SQLite archive
/// lookup behavior so a refactor that changes the selected backup catches at unit speed.
/// </summary>
public sealed class RestoreCommandBuildingTests
{

    [Test]
    public async Task ResolveLatestArchivePicksNewestByMtime()
    {
        // Test-only file staging: three files with distinct mtimes; ResolveLatestArchive
        // must pick the one with the newest timestamp regardless of alphabetical order.
        using var scratch = TestSupport.NewScratchDir("interfold-restore-latest");
        var tmpDir = scratch.Path;
        var oldest = Path.Combine(tmpDir, "20260101-000000.db");
        var middle = Path.Combine(tmpDir, "20260201-000000.db");
        var newest = Path.Combine(tmpDir, "20260301-000000.db");
        await File.WriteAllTextAsync(oldest, "");
        await File.WriteAllTextAsync(middle, "");
        await File.WriteAllTextAsync(newest, "");
        File.SetLastWriteTimeUtc(oldest, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(middle, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newest, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var picked = BackupStoragePaths.LatestFile(tmpDir, "*.db");

        await Assert.That(picked).IsNotNull();
        await Assert.That(picked!.FullName).IsEqualTo(newest);
    }

    [Test]
    public async Task ResolveLatestArchiveReturnsNullOnMissingDirectory()
    {
        // Fresh installs will not have a {backupRoot}/sqlite/ before the first backup.
        // The resolver must return null in that case, not throw — the caller checks for
        // null and surfaces "no archive found" in the operator-facing error.
        var picked = BackupStoragePaths.LatestFile(
            Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")),
            "*.db");

        await Assert.That(picked).IsNull();
    }

    [Test]
    public async Task ResolveLatestArchiveReturnsNullWhenPatternDoesNotMatch()
    {
        // Directory exists but contains only non-matching files.
        using var scratch = TestSupport.NewScratchDir("interfold-restore-nomatch");
        var tmpDir = scratch.Path;
        await File.WriteAllTextAsync(Path.Combine(tmpDir, "readme.txt"), "");

        var picked = BackupStoragePaths.LatestFile(tmpDir, "*.db");

        await Assert.That(picked).IsNull();
    }
}
