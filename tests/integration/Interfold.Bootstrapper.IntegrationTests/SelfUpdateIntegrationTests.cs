using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// End-to-end <c>update-self</c> against a local HTTP release mirror inside DinD.
/// Uses a private copy of the bootstrapper — never the session-shared mount at
/// <see cref="DinDFixtureBase.BootstrapperMountPath"/> (other DinD tests rely on it).
/// Release payloads are real ELF copies (not shell stubs) so post-swap
/// <c>update-self --rollback</c> can still execute.
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
[Explicit]
public sealed class SelfUpdateIntegrationTests(UbuntuDinDFixture dinD)
{
    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task UpdateSelfReplacesBinaryFromLocalReleaseMirror()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(UpdateSelfReplacesBinaryFromLocalReleaseMirror), TestConfigPaths.DefaultConfig);
        var sharedBootstrapper = $"{DinDFixtureBase.BootstrapperMountPath}/interfold-bootstrap";
        var privateBootstrapper = $"{scratch.OutputDir}/interfold-bootstrap";
        var releaseRoot = "/tmp/interfold-fake-release";
        var port = 19876;
        var pidPath = "/tmp/fake-release-http.pid";

        var setup = await FakeReleaseMirror.StartAsync(
            dinD, sharedBootstrapper, privateBootstrapper, releaseRoot, port,
            pidPath, "/tmp/fake-release-http.log");
        await Assert.That(setup.ExitCode).IsEqualTo(0L).Because(setup.Stderr);

        var sharedBefore = await dinD.ExecAsync(["sh", "-c", $"sha256sum {sharedBootstrapper} | awk '{{print $1}}'"]);
        await Assert.That(sharedBefore.ExitCode).IsEqualTo(0L);

        var beforeHash = await dinD.ExecAsync(["sh", "-c", $"sha256sum {privateBootstrapper} | awk '{{print $1}}'"]);
        await Assert.That(beforeHash.ExitCode).IsEqualTo(0L);

        var update = await dinD.ExecAsync([
            "sh", "-c",
            $"INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL=http://127.0.0.1:{port} " +
            $"{privateBootstrapper} update-self --force --non-interactive --output-dir {scratch.OutputDir}"]);
        await Assert.That(update.ExitCode).IsEqualTo(0L).Because(update.Stderr + update.Stdout);

        var oldHash = await dinD.ExecAsync(["sh", "-c", $"sha256sum {privateBootstrapper}.old | awk '{{print $1}}'"]);
        await Assert.That(oldHash.ExitCode).IsEqualTo(0L)
            .Because("update-self must leave the previous binary as {binary}.old");
        await Assert.That(oldHash.Stdout.Trim()).IsEqualTo(beforeHash.Stdout.Trim());

        var sharedAfter = await dinD.ExecAsync(["sh", "-c", $"sha256sum {sharedBootstrapper} | awk '{{print $1}}'"]);
        await Assert.That(sharedAfter.Stdout.Trim()).IsEqualTo(sharedBefore.Stdout.Trim())
            .Because("session-shared mount binary must not be mutated by update-self");

        await FakeReleaseMirror.StopAsync(dinD, pidPath);
    }

    [Test]
    public async Task UpdateSelfRollbackRestoresOldBinary()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(UpdateSelfRollbackRestoresOldBinary), TestConfigPaths.DefaultConfig);
        var sharedBootstrapper = $"{DinDFixtureBase.BootstrapperMountPath}/interfold-bootstrap";
        var privateBootstrapper = $"{scratch.OutputDir}/interfold-bootstrap";
        var releaseRoot = "/tmp/interfold-fake-release-rollback";
        var port = 19877;
        var pidPath = "/tmp/fake-release-http-rollback.pid";

        var setup = await FakeReleaseMirror.StartAsync(
            dinD, sharedBootstrapper, privateBootstrapper, releaseRoot, port,
            pidPath, "/tmp/fake-release-http-rollback.log");
        await Assert.That(setup.ExitCode).IsEqualTo(0L).Because(setup.Stderr);

        var beforeHash = await dinD.ExecAsync(["sh", "-c", $"sha256sum {privateBootstrapper} | awk '{{print $1}}'"]);
        await Assert.That(beforeHash.ExitCode).IsEqualTo(0L);

        var update = await dinD.ExecAsync([
            "sh", "-c",
            $"INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL=http://127.0.0.1:{port} " +
            $"{privateBootstrapper} update-self --force --non-interactive --output-dir {scratch.OutputDir}"]);
        await Assert.That(update.ExitCode).IsEqualTo(0L).Because(update.Stderr);

        var oldExists = await dinD.ExecAsync(["sh", "-c", $"test -f {privateBootstrapper}.old"]);
        await Assert.That(oldExists.ExitCode).IsEqualTo(0L);

        var rollback = await dinD.ExecAsync([
            "sh", "-c",
            $"{privateBootstrapper} update-self --rollback --non-interactive --output-dir {scratch.OutputDir}"]);
        await Assert.That(rollback.ExitCode).IsEqualTo(0L).Because(rollback.Stderr + rollback.Stdout);

        var afterHash = await dinD.ExecAsync(["sh", "-c", $"sha256sum {privateBootstrapper} | awk '{{print $1}}'"]);
        await Assert.That(afterHash.Stdout.Trim()).IsEqualTo(beforeHash.Stdout.Trim())
            .Because("rollback must restore the pre-update bootstrapper");

        var oldGone = await dinD.ExecAsync(["sh", "-c", $"test ! -f {privateBootstrapper}.old"]);
        await Assert.That(oldGone.ExitCode).IsEqualTo(0L);

        await FakeReleaseMirror.StopAsync(dinD, pidPath);
    }
}
