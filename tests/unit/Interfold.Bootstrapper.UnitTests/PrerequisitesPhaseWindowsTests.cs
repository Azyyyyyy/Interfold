using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Phases;
using Interfold.Bootstrapper.UnitTests.Attributes;

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
    [RequiresWindows]
    public async Task EnsureAdministratorNoopsWhenElevated()
    {
        var logger = new PhaseLogger(TestSupport.MakeOptions(outputDir: Path.GetTempPath()));
#pragma warning disable CA1416
        PrerequisitesPhase.EnsureAdministrator(logger, isAdministrator: () => true);
#pragma warning restore CA1416
        await Task.CompletedTask;
    }

    [Test]
    [RequiresWindows]
    public async Task EnsureAdministratorFailsWithNonAdminWhenNotElevated()
    {
        var logger = new PhaseLogger(TestSupport.MakeOptions(outputDir: Path.GetTempPath()));
#pragma warning disable CA1416
        var ex = Assert.Throws<InvalidOperationException>(
            () => PrerequisitesPhase.EnsureAdministrator(logger, isAdministrator: () => false));
#pragma warning restore CA1416
        await Assert.That(ex!.Message).Contains("elevated");
        await Assert.That(ex.Message).Contains("Docker Desktop");
    }

    [Test]
    [RequiresWindows]
    public async Task DockerComposeReadyAsyncDoesNotThrow()
    {
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
}
