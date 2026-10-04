using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Full-coverage scenarios for the bootstrapper running on ubuntu:24.04. All tests share a single
/// <see cref="UbuntuDinDFixture"/> instance for the lifetime of the test session via TUnit's
/// <c>SharedType.PerTestSession</c>, so the expensive publish / pre-pull happens once.
/// Each test allocates a private scratch directory under <c>/opt/scratch</c> via
/// <see cref="DinDFixtureBase.CreateScratchAsync"/>, allowing the suite to run in parallel
/// without races on <c>/opt/deploy</c>.
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
[Explicit]
public class UbuntuBootstrapTests(UbuntuDinDFixture dinD)
{

    // Trust-store-install tests use a variant that enables /usr/local/share/ca-certificates/
    // writes. That path is shared inside the DinD container across all parallel tests, so the
    // tests that exercise it must also opt in to the NotInParallel guard below.

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task ProducesValidComposeOnFreshBox()
    {
        var (scratch, _) = await dinD.PublishAsync(nameof(ProducesValidComposeOnFreshBox), TestConfigPaths.DefaultConfig, "--print-phase-status");

        var composeBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/docker-compose.yaml");
        var compose = Encoding.UTF8.GetString(composeBytes);

        await Assert.That(compose).Contains("interfold-api:").Because("compose missing interfold-api service");
        await Assert.That(compose).Contains("edge-nginx:").Because("compose missing edge-nginx service");
        await Assert.That(compose).DoesNotContain("msg-db:").Because("sqlite persistence must not emit msg-db");
        await Assert.That(compose).DoesNotContain("scylla:").Because("sqlite persistence must not emit scylla");
        await Assert.That(compose).DoesNotContain("${Parameters_").Because("unresolved parameter placeholder leaked into compose");
    }

    // Each compose-up test now binds a private host-port window inside the shared DinD via
    // CreateScratchAsync's port allocator, so concurrent `compose up` calls no longer collide.
    [Test]
    public async Task StackComesUpHealthy()
    {
        var (scratch, _) = await dinD.BootstrapAsync(nameof(StackComesUpHealthy), TestConfigPaths.DefaultConfig);

        var ps = await dinD.ExecAsync(
            ["docker", "compose", "-f", $"{scratch.OutputDir}/docker-compose.yaml", "ps", "--format", "json"]);
        await Assert.That(ps.ExitCode).IsEqualTo(0L);
        await Assert.That(ps.Stdout).Contains("\"State\":\"running\"").Or.Contains("\"Health\":\"healthy\"")
            .Because("expected at least one healthy/running service in compose ps output");
    }

    [Test]
    public async Task IsIdempotentOnRerun()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(IsIdempotentOnRerun), TestConfigPaths.DefaultConfig);

        await dinD.RunOnScratchAsync(scratch, $"{nameof(IsIdempotentOnRerun)}-first", "publish",
            "--print-phase-status");

        var firstHash = await ShaOfComposeAsync(scratch);
        var firstSecrets = await dinD.CopyOutAsync(scratch.SecretsJsonPath);

        var second = await dinD.RunOnScratchAsync(scratch, $"{nameof(IsIdempotentOnRerun)}-second", "publish",
            "--print-phase-status");

        await Assert.That(second.ExitCode).IsEqualTo(0).Because($"second run failed: {second.Stderr}");
        await Assert.That(second.Stderr).Contains("phase=secrets status=skipped")
            .Because("secrets phase should self-skip on a second run");
        await Assert.That(second.Stderr).Contains("phase=certs status=skipped")
            .Because("certs phase should self-skip on a second run");

        var secondHash = await ShaOfComposeAsync(scratch);
        await Assert.That(secondHash).IsEqualTo(firstHash)
            .Because("compose output should be byte-identical across two non-rotating runs");

        var secondSecrets = await dinD.CopyOutAsync(scratch.SecretsJsonPath);
        await Assert.That(Convert.ToHexString(secondSecrets)).IsEqualTo(Convert.ToHexString(firstSecrets))
            .Because("secrets file must be untouched between bootstraps");
    }

    [Test]
    public async Task GeneratedLeafCertHasCorrectSans()
    {
        var (scratch, _) = await dinD.PublishAsync(nameof(GeneratedLeafCertHasCorrectSans), TestConfigPaths.DefaultConfig);

        var leafBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/certs/leaf.crt");
        var rootBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/certs/rootCA.crt");

        using var leaf = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(leafBytes));
        using var root = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(rootBytes));

        var sanExt = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        await Assert.That(sanExt).IsNotNull().Because("leaf cert must include a SubjectAlternativeName extension");
        var sans = sanExt!.EnumerateDnsNames().ToList();
        await Assert.That(sans).Contains("api.test.local");
        await Assert.That(sans).Contains("alt.test.local");

        await Assert.That(leaf.Issuer).IsEqualTo(root.Subject)
            .Because("leaf must be signed by the generated root CA");
    }

    [Test]
    public async Task SecretsFileHasRestrictedPermissions()
    {
        var (scratch, _) = await dinD.PublishAsync(nameof(SecretsFileHasRestrictedPermissions), TestConfigPaths.DefaultConfig);

        var stat = await dinD.ExecAsync(["stat", "-c", "%a %U", scratch.SecretsJsonPath]);
        await Assert.That(stat.ExitCode).IsEqualTo(0L);
        await Assert.That(stat.Stdout.Trim()).StartsWith("600 ")
            .Because("secrets file must be mode 0600");
        await Assert.That(stat.Stdout.Trim()).EndsWith("root")
            .Because("secrets file must be owned by root");
    }

    [Test]
    public async Task RotateSecretsRegeneratesPasswordsAndPreservesCerts()
    {
        var pair = await RunRotateAsync(nameof(RotateSecretsRegeneratesPasswordsAndPreservesCerts), "rotate-secrets");

        await Assert.That(pair.PostSecrets).IsNotEqualTo(pair.PreSecrets).Because("secrets must rotate");
        await Assert.That(pair.PostLeafSha).IsEqualTo(pair.PreLeafSha).Because("certs must remain unchanged on rotate-secrets");
    }

    [Test]
    public async Task RotateCertsRegeneratesCertsAndPreservesSecrets()
    {
        var pair = await RunRotateAsync(nameof(RotateCertsRegeneratesCertsAndPreservesSecrets), "rotate-certs");

        await Assert.That(pair.PostSecrets).IsEqualTo(pair.PreSecrets).Because("secrets must remain unchanged on rotate-certs");
        await Assert.That(pair.PostLeafSha).IsNotEqualTo(pair.PreLeafSha).Because("leaf cert must change on rotate-certs");
    }

    /// <summary>
    /// Common wiring for the two rotate scenarios: publish a scratch deployment, snapshot the
    /// on-disk secrets JSON + leaf-cert SHA, run the supplied rotate command, and snapshot both
    /// again. Callers pick the polarity of the assertions — which pair must change vs which must
    /// be byte-identical — so a future rotate command can plug in with just an extra call site
    /// without cloning the fixture-driving preamble.
    /// </summary>
    private async Task<RotatePair> RunRotateAsync(string testName, string rotateCommand)
    {
        var scratch = await dinD.CreateScratchAsync(testName, TestConfigPaths.DefaultConfig);
        await dinD.RunOnScratchAsync(scratch, $"{testName}-init", "publish");

        var preSecrets = await dinD.ReadSecretsJsonAsync(scratch);
        var preLeafSha = ShaOf(await dinD.CopyOutAsync($"{scratch.OutputDir}/certs/leaf.crt"));

        var rotate = await dinD.RunOnScratchAsync(scratch, testName, rotateCommand);
        await Assert.That(rotate.ExitCode).IsEqualTo(0).Because($"{rotateCommand} failed: {rotate.Stderr}");

        var postSecrets = await dinD.ReadSecretsJsonAsync(scratch);
        var postLeafSha = ShaOf(await dinD.CopyOutAsync($"{scratch.OutputDir}/certs/leaf.crt"));

        return new RotatePair(preSecrets, postSecrets, preLeafSha, postLeafSha);
    }

    private sealed record RotatePair(string PreSecrets, string PostSecrets, string PreLeafSha, string PostLeafSha);

    [Test]
    public async Task RecoversFromInterruptedPublish()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(RecoversFromInterruptedPublish), TestConfigPaths.DefaultConfig);

        // First invocation halts immediately after the secrets phase via the hidden --fault-inject hook.
        var halted = await dinD.RunOnScratchAsync(scratch, $"{nameof(RecoversFromInterruptedPublish)}-halt", "publish",
            "--fault-inject=after-secrets");
        await Assert.That(halted.ExitCode).IsEqualTo(0)
            .Because($"fault-inject halt should exit cleanly: {halted.Stderr}");

        // The secrets file should already exist - confirm it.
        var secretsExist = await dinD.ExecAsync(["test", "-f", scratch.SecretsJsonPath]);
        await Assert.That(secretsExist.ExitCode).IsEqualTo(0L)
            .Because("partial run should have persisted the secrets file");

        // Re-run from scratch - phases that already ran should skip cleanly.
        var resumed = await dinD.RunOnScratchAsync(scratch, $"{nameof(RecoversFromInterruptedPublish)}-resume", "publish",
            "--print-phase-status");
        await Assert.That(resumed.ExitCode).IsEqualTo(0)
            .Because($"resumed run should complete cleanly: {resumed.Stderr}");
        await Assert.That(resumed.Stderr).Contains("phase=secrets status=skipped");

        // Compose YAML must be present after the resumed run.
        var composeExists = await dinD.ExecAsync(["test", "-f", $"{scratch.OutputDir}/docker-compose.yaml"]);
        await Assert.That(composeExists.ExitCode).IsEqualTo(0L);
    }

    // Trust-store install writes /usr/local/share/ca-certificates/interfold-root-ca.crt, which is a
    // process-global filesystem path shared across all tests in this DinD. Serialise via NotInParallel
    // so the file copy + `update-ca-certificates` invocation doesn't race with itself.
    [Test]
    [NotInParallel("ubuntu-trust-install")]
    public async Task RootCaInstalledInDebianTrustStore()
    {
        var (scratch, _) = await dinD.PublishAsync(nameof(RootCaInstalledInDebianTrustStore), TestConfigPaths.TrustInstallConfig);

        // update-ca-certificates places extracted PEMs under /etc/ssl/certs/ symlinked from the anchor.
        var ls = await dinD.ExecAsync(
            ["sh", "-c", "ls /etc/ssl/certs/ | grep -i interfold || true"]);
        await Assert.That(ls.Stdout.Trim()).IsNotEmpty()
            .Because("root CA should have been linked into /etc/ssl/certs after update-ca-certificates");
    }

    private async Task<string> ShaOfComposeAsync(DinDScratch scratch)
    {
        var bytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/docker-compose.yaml");
        return ShaOf(bytes);
    }

    private static string ShaOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
