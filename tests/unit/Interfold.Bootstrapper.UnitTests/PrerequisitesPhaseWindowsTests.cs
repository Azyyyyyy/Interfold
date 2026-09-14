using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Phases;
using Interfold.Bootstrapper.Util;
using TUnit.Core.Exceptions;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class PrerequisitesPhaseWindowsTests
{
    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task NeedsAdministratorTracksDockerComposeReady(bool dockerReady, bool needsAdmin)
    {
        await Assert.That(PrerequisitesPhase.NeedsAdministratorForDockerInstall(dockerReady))
            .IsEqualTo(needsAdmin);
    }

    [Test]
    public async Task EnsureAdministratorNoopsWhenElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new PhaseLogger(TestSupport.MakeOptions(outputDir: Path.GetTempPath()));
#pragma warning disable CA1416
        PrerequisitesPhase.EnsureAdministrator(logger, isAdministrator: () => true);
#pragma warning restore CA1416
        await Task.CompletedTask;
    }

    [Test]
    public async Task EnsureAdministratorFailsWithNonAdminWhenNotElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new PhaseLogger(TestSupport.MakeOptions(outputDir: Path.GetTempPath()));
#pragma warning disable CA1416
        var ex = Assert.Throws<InvalidOperationException>(
            () => PrerequisitesPhase.EnsureAdministrator(logger, isAdministrator: () => false));
#pragma warning restore CA1416
        await Assert.That(ex!.Message).Contains("elevated");
        await Assert.That(ex.Message).Contains("Docker Desktop");
    }

    [Test]
    public async Task DockerComposeReadyAsyncDoesNotThrow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _ = await PrerequisitesPhase.DockerComposeReadyAsync();
    }

    [Test]
    public async Task RefreshProcessPathMergesMachineThenUser()
    {
        string? captured = null;
        PrerequisitesPhase.RefreshProcessPathFromMachine(
            getPath: target => target switch
            {
                EnvironmentVariableTarget.Machine => @"C:\Machine\bin",
                EnvironmentVariableTarget.User => @"C:\User\bin",
                _ => null,
            },
            setProcessPath: value => captured = value);

        await Assert.That(captured).IsEqualTo(@"C:\Machine\bin" + Path.PathSeparator + @"C:\User\bin");
    }

    [Test]
    public async Task RefreshProcessPathUsesMachineAloneWhenUserEmpty()
    {
        string? captured = null;
        PrerequisitesPhase.RefreshProcessPathFromMachine(
            getPath: target => target == EnvironmentVariableTarget.Machine ? @"C:\Machine\bin" : "",
            setProcessPath: value => captured = value);

        await Assert.That(captured).IsEqualTo(@"C:\Machine\bin");
    }

    [Test]
    [Arguments(1, 116_563)]
    [Arguments(3, 249_689)]
    public async Task MinimumContainerAioMatchesPerNodeFormula(int nodes, int expected)
    {
        await Assert.That(PrerequisitesPhase.MinimumContainerAio(nodes)).IsEqualTo(expected);
        await Assert.That(PrerequisitesPhase.IsContainerAioSufficient(expected, nodes)).IsTrue();
        await Assert.That(PrerequisitesPhase.IsContainerAioSufficient(expected - 1, nodes)).IsFalse();
    }

    // Needs a reachable Docker Desktop daemon + alpine pull. Opt in via Explicit.
    [Test]
    [Explicit]
    public async Task ProbeContainerAioReadsAioMaxNrFromDockerDesktopVm()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!await PrerequisitesPhase.DockerComposeReadyAsync())
        {
            throw new SkipTestException("Docker Desktop not on PATH / compose not ready.");
        }

        var probe = await ProcessRunner.RunAsync(
            "docker",
            ["run", "--rm", "alpine", "cat", "/proc/sys/fs/aio-max-nr"]);
        if (probe.ExitCode != 0 || !int.TryParse(probe.StdOut.Trim(), out var current))
        {
            throw new SkipTestException(
                $"Could not read fs.aio-max-nr via alpine (exit={probe.ExitCode}): {probe.StdErr}");
        }

        await Assert.That(current).IsGreaterThan(0);

        var logger = new PhaseLogger(TestSupport.MakeOptions(outputDir: Path.GetTempPath()));
        await PrerequisitesPhase.ProbeContainerAioAsync(scyllaNodes: 1, logger, CancellationToken.None);
    }
}
