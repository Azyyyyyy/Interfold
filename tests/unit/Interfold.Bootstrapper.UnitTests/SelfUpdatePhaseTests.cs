using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class SelfUpdatePhaseTests
{
    [Test]
    public async Task BuildUpdateExecStartChainsSelfUpdateWhenEnabled()
    {
        var config = TestSupport.MakeConfig();
        config.Deployment.Update.Bootstrapper.Enabled = true;
        config.Deployment.Update.Bootstrapper.Channel = BootstrapperReleaseChannel.BleedingEdge;

        var exec = SystemdInstallPhase.BuildUpdateExecStart(
            config,
            "/opt/interfold/interfold-bootstrap",
            "/opt/interfold/interfold.bootstrap.json",
            "/opt/interfold/deploy");

        await Assert.That(exec).Contains("update-self --non-interactive --channel bleeding-edge");
        await Assert.That(exec).Contains("exec /opt/interfold/interfold-bootstrap update-images");
    }

    [Test]
    public async Task BuildUpdateExecStartSkipsSelfUpdateWhenDisabled()
    {
        var config = TestSupport.MakeConfig();
        config.Deployment.Update.Bootstrapper.Enabled = false;

        var configPath = "/opt/interfold/interfold.bootstrap.json";
        var exec = SystemdInstallPhase.BuildUpdateExecStart(
            config,
            "/opt/interfold/interfold-bootstrap",
            configPath,
            "/opt/interfold/deploy");

        await Assert.That(exec).IsEqualTo(
            $"/opt/interfold/interfold-bootstrap update-images --config {Path.GetFullPath(configPath)} --output-dir /opt/interfold/deploy");
        await Assert.That(exec).DoesNotContain("update-self");
    }

    [Test]
    public async Task RollbackRestoresOldBinary()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var scratch = TestSupport.NewScratchDir("bootstrapper-rollback");
        var live = Path.Combine(scratch.Path, "interfold-bootstrap");
        var old = Path.Combine(scratch.Path, "interfold-bootstrap.old");
        await File.WriteAllTextAsync(live, "new-binary");
        await File.WriteAllTextAsync(old, "old-binary");

        var logger = new PhaseLogger(TestSupport.MakeOptions(outputDir: scratch.Path, nonInteractive: true));
        var result = SelfUpdatePhaseCore.Rollback(live, logger);

        await Assert.That(result).IsEqualTo(SelfUpdateResult.RolledBack);
        await Assert.That(await File.ReadAllTextAsync(live)).IsEqualTo("old-binary");
        await Assert.That(File.Exists(old)).IsFalse();
    }

    [Test]
    public async Task IsUpdateAvailableComparesSemanticVersions()
    {
        await Assert.That(BootstrapperVersion.IsUpdateAvailable("1.2.0", force: false)).IsTrue();
        await Assert.That(BootstrapperVersion.IsUpdateAvailable("0.0.0-dev", force: false)).IsFalse();
        await Assert.That(BootstrapperVersion.IsUpdateAvailable("0.0.0-dev", force: true)).IsTrue();
    }
}
