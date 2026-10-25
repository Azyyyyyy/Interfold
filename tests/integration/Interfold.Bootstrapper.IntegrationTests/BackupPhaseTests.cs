using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Integration tests for the <c>backup</c> subcommand against a real running compose stack
/// inside the shared Ubuntu DinD fixture. Each test runs a full <c>bootstrap</c> first,
/// then exercises the backup pipeline and asserts the on-disk artifacts.
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
[Explicit]
public class BackupPhaseTests(UbuntuDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task BackupCreatesSqliteArtifacts()
    {
        var (scratch, _) = await dinD.BootstrapAsync(nameof(BackupCreatesSqliteArtifacts), TestConfigPaths.DefaultConfig);

        var backup = await dinD.RunOnScratchAsync(scratch, nameof(BackupCreatesSqliteArtifacts), "backup",
            "--component", "all");
        await Assert.That(backup.ExitCode).IsEqualTo(0).Because($"backup failed: {backup.Stderr}");

        var sqliteList = await dinD.ExecAsync(["sh", "-c", $"ls -1 {scratch.OutputDir}/backups/sqlite/*.db 2>/dev/null | head -5"]);
        await Assert.That(sqliteList.ExitCode).IsEqualTo(0L);
        await Assert.That(sqliteList.Stdout.Trim().Length).IsGreaterThan(0)
            .Because("backup should produce at least one .db under backups/sqlite/");

        var sqliteSize = await dinD.ExecAsync(["sh", "-c", $"stat -c %s {scratch.OutputDir}/backups/sqlite/*.db | head -1"]);
        await Assert.That(int.Parse(sqliteSize.Stdout.Trim())).IsGreaterThan(100)
            .Because("sqlite backup output must be non-trivial in size");
    }

    [Test]
    public async Task BackupRetentionPrunesOldestPastRetainCount()
    {
        var (scratch, _) = await dinD.BootstrapAsync(nameof(BackupRetentionPrunesOldestPastRetainCount), TestConfigPaths.DefaultConfig);

        for (var i = 0; i < 2; i++)
        {
            await dinD.ExecAsync(["sleep", "1"]);
            var b = await dinD.RunOnScratchAsync(scratch, $"{nameof(BackupRetentionPrunesOldestPastRetainCount)}-{i}", "backup",
                "--component", "all", "--retain", "2");
            await Assert.That(b.ExitCode).IsEqualTo(0).Because($"backup #{i} failed: {b.Stderr}");
        }

        var countAfterTwo = await dinD.CountFilesAsync(scratch, "backups/sqlite/");
        await Assert.That(countAfterTwo).IsEqualTo(2)
            .Because("two backups + retain=2 should leave exactly 2 files");

        await dinD.ExecAsync(["sleep", "1"]);
        var third = await dinD.RunOnScratchAsync(scratch, $"{nameof(BackupRetentionPrunesOldestPastRetainCount)}-third", "backup",
            "--component", "all", "--retain", "2");
        await Assert.That(third.ExitCode).IsEqualTo(0).Because($"third backup failed: {third.Stderr}");

        var countAfterThree = await dinD.CountFilesAsync(scratch, "backups/sqlite/");
        await Assert.That(countAfterThree).IsEqualTo(2)
            .Because("three backups + retain=2 should prune the oldest, leaving exactly 2");
    }

    [Test]
    public async Task BackupComponentFlagRestrictsScope()
    {
        var (scratch, _) = await dinD.BootstrapAsync(nameof(BackupComponentFlagRestrictsScope), TestConfigPaths.DefaultConfig);

        var sqliteOnly = await dinD.RunOnScratchAsync(scratch, $"{nameof(BackupComponentFlagRestrictsScope)}-sqlite", "backup",
            "--component", "sqlite");
        await Assert.That(sqliteOnly.ExitCode).IsEqualTo(0).Because(sqliteOnly.Stderr);

        var sqliteList = await dinD.CountFilesAsync(scratch, "backups/sqlite/*.db");
        await Assert.That(sqliteList).IsEqualTo(1);
    }

    [Test]
    public async Task BackupWithoutComposeFailsClearly()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(BackupWithoutComposeFailsClearly), TestConfigPaths.DefaultConfig);

        var result = await dinD.RunOnScratchAsync(scratch, nameof(BackupWithoutComposeFailsClearly), "backup",
            "--component", "sqlite");

        await Assert.That(result.ExitCode).IsNotEqualTo(0)
            .Because("backup against a bare scratch (no compose, no secrets) must fail");

        var combined = result.Stdout + result.Stderr;
        await Assert.That(combined).Contains("docker-compose.yaml").Or.Contains("secrets/secrets.json")
            .Because("error must name a specific missing artifact");
        await Assert.That(combined).Contains("bootstrap")
            .Because("error must guide the operator to `bootstrap` as the fix");
    }
}
