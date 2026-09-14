using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Native Windows coverage for <c>install-service</c>: Task Scheduler XML dry-run
/// (<c>--systemd-unit-dir</c>) and live <c>schtasks /Create</c>. Linux DinD covers
/// the systemd path in <see cref="SystemdInstallTests"/>; this class is skipped
/// off Windows via <see cref="RequiresWindowsAttribute"/>.
/// </summary>
[RequiresWindows]
[Explicit]
public sealed class WindowsScheduledTaskInstallTests
{
    // Frozen operator contract — keep aligned with WindowsTaskNames.
    private const string InterfoldTask = "Interfold";
    private const string BackupTask = "InterfoldBackup";

    [Test]
    public async Task DryRunWritesBothTaskXmlFiles()
    {
        await using var scratch = await WindowsInstallScratch.CreateAsync(nameof(DryRunWritesBothTaskXmlFiles));
        Directory.CreateDirectory(scratch.TaskXmlDir);

        var result = await scratch.RunInstallServiceAsync("--systemd-unit-dir", scratch.TaskXmlDir);
        await Assert.That(result.ExitCode).IsEqualTo(0)
            .Because($"install-service dry-run failed: {result.Stderr}\n{result.Stdout}");
        await Assert.That(result.Stdout + result.Stderr).Contains("skipping schtasks");

        await Assert.That(File.Exists(Path.Combine(scratch.TaskXmlDir, "interfold.xml"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(scratch.TaskXmlDir, "interfold-backup.xml"))).IsTrue();
    }

    [Test]
    public async Task DryRunXmlContainsExpectedTokens()
    {
        await using var scratch = await WindowsInstallScratch.CreateAsync(nameof(DryRunXmlContainsExpectedTokens));
        Directory.CreateDirectory(scratch.TaskXmlDir);

        var result = await scratch.RunInstallServiceAsync("--systemd-unit-dir", scratch.TaskXmlDir);
        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr + result.Stdout);

        var boot = await WindowsInstallScratch.ReadUtf16Async(Path.Combine(scratch.TaskXmlDir, "interfold.xml"));
        await Assert.That(boot).Contains("compose -f");
        await Assert.That(boot).Contains("up -d");
        await Assert.That(boot).Contains("<LogonTrigger>");
        await Assert.That(boot).Contains("<RunLevel>LeastPrivilege</RunLevel>");
        await Assert.That(boot).Contains("<UserId>");
        await Assert.That(boot).DoesNotContain("Highest");
        await Assert.That(boot).DoesNotContain("<URI>");
        await Assert.That(boot).Contains(scratch.OutputDir);

        var backup = await WindowsInstallScratch.ReadUtf16Async(Path.Combine(scratch.TaskXmlDir, "interfold-backup.xml"));
        await Assert.That(backup).Contains(scratch.BinaryPath);
        await Assert.That(backup).Contains("backup --config");
        await Assert.That(backup).Contains(scratch.ConfigPath);
        await Assert.That(backup).Contains(scratch.OutputDir);
        await Assert.That(backup).Contains("<CalendarTrigger>");
        await Assert.That(backup).DoesNotContain("update-images");
        await Assert.That(backup).DoesNotContain("<URI>");
    }

    [Test]
    public async Task DryRunChainsUpdateActionWhenEnabled()
    {
        await using var scratch = await WindowsInstallScratch.CreateAsync(nameof(DryRunChainsUpdateActionWhenEnabled));
        Directory.CreateDirectory(scratch.TaskXmlDir);
        await scratch.SetUpdateEnabledAsync(true);

        var result = await scratch.RunInstallServiceAsync("--systemd-unit-dir", scratch.TaskXmlDir);
        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr + result.Stdout);

        var backup = await WindowsInstallScratch.ReadUtf16Async(Path.Combine(scratch.TaskXmlDir, "interfold-backup.xml"));
        await Assert.That(backup).Contains("update-images --config");
        await Assert.That(backup.IndexOf("backup --config", StringComparison.Ordinal))
            .IsLessThan(backup.IndexOf("update-images --config", StringComparison.Ordinal));
    }

    [Test]
    public async Task UntranslatableScheduleFailsInstallService()
    {
        await using var scratch = await WindowsInstallScratch.CreateAsync(nameof(UntranslatableScheduleFailsInstallService));
        Directory.CreateDirectory(scratch.TaskXmlDir);
        await scratch.SetBackupScheduleAsync("Mon *-*-* 03:00:00");

        var result = await scratch.RunInstallServiceAsync("--systemd-unit-dir", scratch.TaskXmlDir);
        await Assert.That(result.ExitCode).IsNotEqualTo(0);
        var combined = result.Stdout + result.Stderr;
        await Assert.That(combined).Contains("invalid-calendar")
            .Or.Contains("cannot be translated to Task Scheduler");
    }

    [Test]
    [NotInParallel("windows-schtasks")]
    public async Task RegistersTasksWithSchtasksAndCleansUp()
    {
        await using var scratch = await WindowsInstallScratch.CreateAsync(nameof(RegistersTasksWithSchtasksAndCleansUp));
        await WindowsInstallScratch.DeleteTaskIfPresentAsync(InterfoldTask);
        await WindowsInstallScratch.DeleteTaskIfPresentAsync(BackupTask);

        try
        {
            // No --enable-autostart: avoid docker compose up on a host without a stack.
            var result = await scratch.RunInstallServiceAsync();
            var combined = result.Stdout + result.Stderr;
            // InteractiveToken + LogonTrigger XML import is denied on some hosts
            // (locked-down workstations, non-interactive agent sessions). Dry-run XML
            // tests above remain the hard contract; GHA windows-latest is the live gate.
            if (result.ExitCode != 0
                && combined.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            {
                Skip.Test(
                    "Host denied schtasks /Create for InteractiveToken XML; " +
                    "dry-run XML coverage still applies.");
            }

            await Assert.That(result.ExitCode).IsEqualTo(0)
                .Because($"live install-service failed: {result.Stderr}\n{result.Stdout}");
            await Assert.That(combined).Contains($"registered scheduled task {InterfoldTask}");
            await Assert.That(combined).Contains($"registered scheduled task {BackupTask}");

            var queryBoot = await WindowsInstallScratch.SchtasksAsync("/Query", "/TN", InterfoldTask, "/FO", "LIST", "/V");
            await Assert.That(queryBoot.ExitCode).IsEqualTo(0).Because(queryBoot.Stderr + queryBoot.Stdout);
            await Assert.That(queryBoot.Stdout).Contains("Least Privilege")
                .Or.Contains("LeastPrivilege")
                .Or.Contains("Limited");

            var queryBackup = await WindowsInstallScratch.SchtasksAsync("/Query", "/TN", BackupTask, "/FO", "LIST", "/V");
            await Assert.That(queryBackup.ExitCode).IsEqualTo(0).Because(queryBackup.Stderr + queryBackup.Stdout);
            await Assert.That(queryBackup.Stdout + queryBackup.Stderr).Contains("backup");
        }
        finally
        {
            await WindowsInstallScratch.DeleteTaskIfPresentAsync(InterfoldTask);
            await WindowsInstallScratch.DeleteTaskIfPresentAsync(BackupTask);
        }
    }
}
