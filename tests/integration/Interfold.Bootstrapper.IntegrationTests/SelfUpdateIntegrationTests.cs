using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// End-to-end <c>update-self</c> against a local HTTP release mirror inside DinD.
/// Uses a private copy of the bootstrapper — never the session-shared mount at
/// <see cref="DinDFixtureBase.BootstrapperMountPath"/> (other DinD tests rely on it).
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

        var setup = await dinD.ExecAsync(["sh", "-c", $$"""
            set -e
            cp -f {{sharedBootstrapper}} {{privateBootstrapper}}
            chmod +x {{privateBootstrapper}}
            rm -rf {{releaseRoot}}
            mkdir -p {{releaseRoot}}/latest
            echo '9.9.9-test' > {{releaseRoot}}/latest/version.txt
            printf '#!/bin/sh\necho replaced-bootstrap\n' > /tmp/replacement-bootstrap
            chmod +x /tmp/replacement-bootstrap
            # Entry must be named interfold-bootstrap so ExtractBootstrapperFromTarball
            # writes the expected binary content into the private copy path.
            cp -f /tmp/replacement-bootstrap /tmp/interfold-bootstrap
            tar -czf {{releaseRoot}}/latest/interfold-bootstrap-linux-x64.tar.gz -C /tmp interfold-bootstrap
            hash=$(sha256sum {{releaseRoot}}/latest/interfold-bootstrap-linux-x64.tar.gz | awk '{print $1}')
            echo "$hash  interfold-bootstrap-linux-x64.tar.gz" > {{releaseRoot}}/latest/SHA256SUMS
            python3 -m http.server {{port}} --directory {{releaseRoot}} >/tmp/fake-release-http.log 2>&1 &
            echo $! > /tmp/fake-release-http.pid
            """]);
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

        var afterHash = await dinD.ExecAsync(["sh", "-c", $"sha256sum {privateBootstrapper} | awk '{{print $1}}'"]);
        await Assert.That(afterHash.ExitCode).IsEqualTo(0L);
        await Assert.That(afterHash.Stdout.Trim()).IsNotEqualTo(beforeHash.Stdout.Trim())
            .Because("update-self must replace the private bootstrapper binary");

        var oldExists = await dinD.ExecAsync(["sh", "-c", $"test -f {privateBootstrapper}.old"]);
        await Assert.That(oldExists.ExitCode).IsEqualTo(0L);

        var sharedAfter = await dinD.ExecAsync(["sh", "-c", $"sha256sum {sharedBootstrapper} | awk '{{print $1}}'"]);
        await Assert.That(sharedAfter.Stdout.Trim()).IsEqualTo(sharedBefore.Stdout.Trim())
            .Because("session-shared mount binary must not be mutated by update-self");

        await dinD.ExecAsync(["sh", "-c", "kill $(cat /tmp/fake-release-http.pid) 2>/dev/null || true"]);
    }

    [Test]
    public async Task UpdateSelfRollbackRestoresOldBinary()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(UpdateSelfRollbackRestoresOldBinary), TestConfigPaths.DefaultConfig);
        var sharedBootstrapper = $"{DinDFixtureBase.BootstrapperMountPath}/interfold-bootstrap";
        var privateBootstrapper = $"{scratch.OutputDir}/interfold-bootstrap";
        var releaseRoot = "/tmp/interfold-fake-release-rollback";
        var port = 19877;

        var setup = await dinD.ExecAsync(["sh", "-c", $$"""
            set -e
            cp -f {{sharedBootstrapper}} {{privateBootstrapper}}
            chmod +x {{privateBootstrapper}}
            rm -rf {{releaseRoot}}
            mkdir -p {{releaseRoot}}/latest
            echo '9.9.9-test' > {{releaseRoot}}/latest/version.txt
            printf '#!/bin/sh\necho replaced-bootstrap\n' > /tmp/replacement-bootstrap
            chmod +x /tmp/replacement-bootstrap
            cp -f /tmp/replacement-bootstrap /tmp/interfold-bootstrap
            tar -czf {{releaseRoot}}/latest/interfold-bootstrap-linux-x64.tar.gz -C /tmp interfold-bootstrap
            hash=$(sha256sum {{releaseRoot}}/latest/interfold-bootstrap-linux-x64.tar.gz | awk '{print $1}')
            echo "$hash  interfold-bootstrap-linux-x64.tar.gz" > {{releaseRoot}}/latest/SHA256SUMS
            python3 -m http.server {{port}} --directory {{releaseRoot}} >/tmp/fake-release-http-rollback.log 2>&1 &
            echo $! > /tmp/fake-release-http-rollback.pid
            """]);
        await Assert.That(setup.ExitCode).IsEqualTo(0L).Because(setup.Stderr);

        var beforeHash = await dinD.ExecAsync(["sh", "-c", $"sha256sum {privateBootstrapper} | awk '{{print $1}}'"]);
        await Assert.That(beforeHash.ExitCode).IsEqualTo(0L);

        var update = await dinD.ExecAsync([
            "sh", "-c",
            $"INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL=http://127.0.0.1:{port} " +
            $"{privateBootstrapper} update-self --force --non-interactive --output-dir {scratch.OutputDir}"]);
        await Assert.That(update.ExitCode).IsEqualTo(0L).Because(update.Stderr);

        var rollback = await dinD.ExecAsync([
            "sh", "-c",
            $"{privateBootstrapper} update-self --rollback --non-interactive --output-dir {scratch.OutputDir}"]);
        await Assert.That(rollback.ExitCode).IsEqualTo(0L).Because(rollback.Stderr);

        var afterHash = await dinD.ExecAsync(["sh", "-c", $"sha256sum {privateBootstrapper} | awk '{{print $1}}'"]);
        await Assert.That(afterHash.Stdout.Trim()).IsEqualTo(beforeHash.Stdout.Trim())
            .Because("rollback must restore the pre-update bootstrapper");

        await dinD.ExecAsync(["sh", "-c", "kill $(cat /tmp/fake-release-http-rollback.pid) 2>/dev/null || true"]);
    }
}
