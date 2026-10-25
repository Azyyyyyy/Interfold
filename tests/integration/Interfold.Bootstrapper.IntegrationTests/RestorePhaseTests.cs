using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Integration tests for the <c>restore</c> subcommand against a real running compose stack
/// inside the shared Ubuntu DinD fixture. Each test runs a full <c>bootstrap</c>,
/// takes a backup, mutates the live database file, then restores and asserts
/// the database is restored.
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
[Explicit]
public class RestorePhaseTests(UbuntuDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task RestoreSqliteFromArchiveRoundTrips()
    {
        var (scratch, composeFile) = await dinD.BootstrapAsync(nameof(RestoreSqliteFromArchiveRoundTrips), TestConfigPaths.DefaultConfig);

        var backup = await dinD.RunOnScratchAsync(scratch, $"{nameof(RestoreSqliteFromArchiveRoundTrips)}-backup", "backup",
            "--component", "sqlite");
        await Assert.That(backup.ExitCode).IsEqualTo(0).Because($"backup failed: {backup.Stderr}");

        var dbPath = $"{scratch.OutputDir}/data/sqlite/interfold.db";
        var rm = await dinD.ExecAsync(["rm", "-f", dbPath]);
        await Assert.That(rm.ExitCode).IsEqualTo(0L);

        var existsBefore = await dinD.ExecAsync(["test", "-f", dbPath]);
        await Assert.That(existsBefore.ExitCode).IsNotEqualTo(0L)
            .Because("database must be absent before restore");

        var restore = await dinD.RunOnScratchAsync(scratch, nameof(RestoreSqliteFromArchiveRoundTrips), "restore",
            "--restore-latest", "--force");
        await Assert.That(restore.ExitCode).IsEqualTo(0).Because($"restore failed: {restore.Stderr}");

        var existsAfter = await dinD.ExecAsync(["test", "-f", dbPath]);
        await Assert.That(existsAfter.ExitCode).IsEqualTo(0L)
            .Because("database file must be present after restore");
    }

    [Test]
    public async Task RestoreLatestPicksMostRecentArchive()
    {
        var (scratch, _) = await dinD.BootstrapAsync(nameof(RestoreLatestPicksMostRecentArchive), TestConfigPaths.DefaultConfig);

        var b1 = await dinD.RunOnScratchAsync(scratch, $"{nameof(RestoreLatestPicksMostRecentArchive)}-b1", "backup",
            "--component", "sqlite");
        await Assert.That(b1.ExitCode).IsEqualTo(0).Because(b1.Stderr);

        await dinD.ExecAsync(["sleep", "2"]);

        var b2 = await dinD.RunOnScratchAsync(scratch, $"{nameof(RestoreLatestPicksMostRecentArchive)}-b2", "backup",
            "--component", "sqlite");
        await Assert.That(b2.ExitCode).IsEqualTo(0).Because(b2.Stderr);

        var newestList = await dinD.ExecAsync(["sh", "-c",
            $"ls -1t {scratch.OutputDir}/backups/sqlite/*.db | head -1"]);
        await Assert.That(newestList.ExitCode).IsEqualTo(0L);
        var newestArchive = newestList.Stdout.Trim();
        await Assert.That(newestArchive.Length).IsGreaterThan(0)
            .Because("there must be at least one archive present after two backup runs");

        var restore = await dinD.RunOnScratchAsync(scratch, nameof(RestoreLatestPicksMostRecentArchive), "restore",
            "--restore-latest", "--force");
        await Assert.That(restore.ExitCode).IsEqualTo(0).Because($"restore failed: {restore.Stderr}");

        var combined = restore.Stdout + restore.Stderr;
        await Assert.That(combined).Contains(Path.GetFileName(newestArchive))
            .Because("--restore-latest must resolve to the newest archive by mtime");
    }

    [Test]
    public async Task RestoreWithoutArchivesFailsClearly()
    {
        var (scratch, _) = await dinD.BootstrapAsync(nameof(RestoreWithoutArchivesFailsClearly), TestConfigPaths.DefaultConfig);

        var result = await dinD.RunOnScratchAsync(scratch, nameof(RestoreWithoutArchivesFailsClearly), "restore",
            "--restore-latest", "--force");

        await Assert.That(result.ExitCode).IsNotEqualTo(0)
            .Because("restore --restore-latest with no archives on disk must fail");

        var combined = result.Stdout + result.Stderr;
        await Assert.That(combined).Contains("restore-sqlite").Or.Contains("archives")
            .Because("error must name the missing selectors or archives");
    }

    [Test]
    public async Task RestoreInNonInteractiveModeRequiresForce()
    {
        var (scratch, _) = await dinD.BootstrapAsync(nameof(RestoreInNonInteractiveModeRequiresForce), TestConfigPaths.DefaultConfig);

        var backup = await dinD.RunOnScratchAsync(scratch, $"{nameof(RestoreInNonInteractiveModeRequiresForce)}-backup", "backup",
            "--component", "sqlite");
        await Assert.That(backup.ExitCode).IsEqualTo(0).Because(backup.Stderr);

        var result = await dinD.RunOnScratchAsync(scratch, nameof(RestoreInNonInteractiveModeRequiresForce), "restore",
            "--restore-latest");

        await Assert.That(result.ExitCode).IsNotEqualTo(0)
            .Because("restore in non-interactive mode without --force must be refused");

        var combined = result.Stdout + result.Stderr;
        await Assert.That(combined).Contains("--force")
            .Because("error must name --force as the escape hatch for non-interactive mode");
    }
}
