using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Integration tests for the <c>update-images</c> subcommand against a real running compose
/// stack inside the shared Ubuntu DinD fixture. Each test runs a full <c>bootstrap</c> first,
/// then exercises the update pipeline (pull + optional recreate + health check + retention)
/// and asserts on the on-disk backup archives, compose state, and exit codes.
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
[Explicit]
public class UpdateImagesPhaseTests(UbuntuDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task UpdateWithNoChangedImagesIsNoOp()
    {
        var (scratch, _) = await dinD.BootstrapAsync(nameof(UpdateWithNoChangedImagesIsNoOp), TestConfigPaths.DefaultConfig);

        var beforeId = await dinD.ExecAsync(["sh", "-c",
            $"docker compose -f {scratch.OutputDir}/docker-compose.yaml ps -q interfold-api"]);
        await Assert.That(beforeId.ExitCode).IsEqualTo(0L);
        var apiIdBefore = beforeId.Stdout.Trim();
        await Assert.That(apiIdBefore.Length).IsGreaterThan(0)
            .Because("bootstrap should have produced a running interfold-api container");

        var update = await dinD.RunOnScratchAsync(scratch, nameof(UpdateWithNoChangedImagesIsNoOp), "update-images",
            "--service", "edge-nginx");
        await Assert.That(update.ExitCode).IsEqualTo(0).Because($"update-images failed: {update.Stderr}");

        var combined = update.Stdout + update.Stderr;
        await Assert.That(combined).Contains("no-op")
            .Because("with no image changes the phase must log a no-op branch and skip recreate");

        var afterId = await dinD.ExecAsync(["sh", "-c",
            $"docker compose -f {scratch.OutputDir}/docker-compose.yaml ps -q interfold-api"]);
        await Assert.That(afterId.Stdout.Trim()).IsEqualTo(apiIdBefore)
            .Because("no-op update must not recreate the api container");
    }

    [Test]
    public async Task UpdatePerformsPreUpdateBackup()
    {
        var (scratch, _) = await dinD.BootstrapAsync(nameof(UpdatePerformsPreUpdateBackup), TestConfigPaths.DefaultConfig);

        var sqliteBefore = await dinD.CountFilesAsync(scratch, "backups/sqlite/*.db");
        await Assert.That(sqliteBefore).IsEqualTo(0)
            .Because("baseline: bootstrap should not have taken any backups yet");

        var update = await dinD.RunOnScratchAsync(scratch, nameof(UpdatePerformsPreUpdateBackup), "update-images",
            "--service", "edge-nginx");
        await Assert.That(update.ExitCode).IsEqualTo(0).Because($"update-images failed: {update.Stderr}");

        var sqliteAfter = await dinD.CountFilesAsync(scratch, "backups/sqlite/*.db");
        await Assert.That(sqliteAfter).IsEqualTo(1)
            .Because("update-images must take a pre-update sqlite backup");
    }

    [Test]
    public async Task UpdateWithSkipPreUpdateBackupDoesNotWriteArchives()
    {
        var (scratch, _) = await dinD.BootstrapAsync(nameof(UpdateWithSkipPreUpdateBackupDoesNotWriteArchives), TestConfigPaths.DefaultConfig);

        var update = await dinD.RunOnScratchAsync(scratch, nameof(UpdateWithSkipPreUpdateBackupDoesNotWriteArchives), "update-images",
            "--service", "edge-nginx",
            "--skip-pre-update-backup");
        await Assert.That(update.ExitCode).IsEqualTo(0).Because($"update-images failed: {update.Stderr}");

        var sqliteAfter = await dinD.CountFilesAsync(scratch, "backups/sqlite/*.db");
        await Assert.That(sqliteAfter).IsEqualTo(0)
            .Because("--skip-pre-update-backup must suppress the backup step");

        await Assert.That(update.Stdout + update.Stderr).Contains("skip-pre-update-backup")
            .Because("the phase must warn loudly when the escape hatch is used");
    }

    [Test]
    public async Task UpdateWithoutComposeFailsClearly()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(UpdateWithoutComposeFailsClearly), TestConfigPaths.DefaultConfig);

        var result = await dinD.RunOnScratchAsync(scratch, nameof(UpdateWithoutComposeFailsClearly), "update-images");

        await Assert.That(result.ExitCode).IsNotEqualTo(0)
            .Because("update-images against a bare scratch must fail");

        var combined = result.Stdout + result.Stderr;
        await Assert.That(combined).Contains("bootstrap")
            .Because("error must guide the operator to run `bootstrap` first");
    }
}
