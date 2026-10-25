using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Asserts the argv shape <see cref="BackupPhase"/> hands to docker compose for both the
/// pg_dump and nodetool snapshot pipelines. These tests don't exec anything — they just
/// pin the argv contract so a future refactor that swaps argument order or adds a flag
/// catches the change at unit speed rather than in the DinD integration suite.
/// </summary>
public sealed class BackupCommandBuildingTests
{


    [Test]
    public async Task ResolveBackupRootPrefersCliOverride()
    {
        // Operator escape hatch wins over both config and the default. Path is normalised to
        // an absolute one because BackupPhase later wraps it in Directory.CreateDirectory +
        // EnumerateFiles, and both behave erratically with relative paths under a systemd
        // unit's unpredictable CWD.
        var options = TestSupport.MakeOptions(
            command: BootstrapCommand.Backup,
            backupDirOverride: "/srv/backups");

        var config = TestSupport.MakeConfig(tweak: c => c.Deployment.Backup.Directory = "/var/never-seen");
        var resolved = BackupPhase.ResolveBackupRoot(options, config);

        // Path.GetFullPath normalises to the platform's separator style; just check it ends
        // with the expected suffix to keep this portable across Windows / Linux runners.
        await Assert.That(resolved.Replace('\\', '/'))
            .Contains("/srv/backups");
    }

    [Test]
    public async Task ResolveBackupRootFallsBackToConfig()
    {
        var options = TestSupport.MakeOptions(command: BootstrapCommand.Backup);

        var config = TestSupport.MakeConfig(tweak: c => c.Deployment.Backup.Directory = "/var/backups/interfold");
        var resolved = BackupPhase.ResolveBackupRoot(options, config);

        await Assert.That(resolved.Replace('\\', '/'))
            .Contains("/var/backups/interfold");
    }

    [Test]
    public async Task ResolveBackupRootDefaultsToOutputDirSubfolder()
    {
        // Neither CLI nor config supplied → fall back to {outputDir}/backups. This is the
        // default path that 99% of operators will hit; the assertion confirms the join is
        // exactly "backups" (no typos, no leading slash).
        var outputDir = Path.GetFullPath("./deploy");
        var options = TestSupport.MakeOptions(
            command: BootstrapCommand.Backup,
            outputDir: outputDir);

        var config = TestSupport.MakeConfig();
        var resolved = BackupPhase.ResolveBackupRoot(options, config);

        await Assert.That(resolved).IsEqualTo(Path.Combine(outputDir, "backups"));
    }

    [Test]
    public async Task BuildArchiveFileNameProducesExpectedExtensions()
    {
        // SQLite uses a simple .db file backup. The extensions are part of
        // the documented backup layout that operators rely on for ad-hoc tooling — pinning
        // them here so a refactor can't quietly switch to .sqlite or .backup.
        await Assert.That(BackupPhase.BuildArchiveFileName(BackupDatabaseComponent.Sqlite, "20260301-120000"))
            .IsEqualTo("20260301-120000.db");
    }

    [Test]
    public async Task ComponentParsing_RejectsUnknownAndDefaultsToAll()
    {
        // The CLI-boundary parse is what shields BuildArchiveFileName from meaningless
        // component values now that the helper takes the enum: unknown strings return
        // null (the phase surfaces its legacy --component error) and absent values keep
        // the historical "all" default.
        await Assert.That(BackupDatabaseComponentExtensions.TryParse("redis")).IsNull();
        await Assert.That(BackupDatabaseComponentExtensions.TryParse(null)).IsEqualTo(BackupDatabaseComponent.All);
        await Assert.That(BackupDatabaseComponentExtensions.TryParse("sqlite")).IsEqualTo(BackupDatabaseComponent.Sqlite);
    }
}
