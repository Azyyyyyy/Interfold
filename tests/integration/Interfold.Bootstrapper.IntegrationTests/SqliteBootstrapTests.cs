using System.Text;
using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// End-to-end sqlite persistence path: publish omits CQL/Postgres, db-init seeds a host-side
/// <c>interfold.db</c>, launch brings API+edge up against that file, and backup copies it.
/// Shares <see cref="UbuntuDinDFixture"/> across suites.
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
[Explicit]
public class SqliteBootstrapTests(UbuntuDinDFixture dinD)
{
    private const string SqliteDbRelativePath = "data/sqlite/interfold.db";

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task PublishOmitsCqlAndPostgresAndBindMountsSqlite()
    {
        var (scratch, _) = await dinD.PublishAsync(
            nameof(PublishOmitsCqlAndPostgresAndBindMountsSqlite),
            TestConfigPaths.SqliteConfig,
            "--skip-prereqs");

        var compose = Encoding.UTF8.GetString(
            await dinD.CopyOutAsync($"{scratch.OutputDir}/docker-compose.yaml"));

        await Assert.That(compose).Contains($"{ComposeServices.InterfoldApi}:")
            .Because("compose missing interfold-api service");
        await Assert.That(compose).Contains($"{ComposeServices.EdgeNginx}:")
            .Because("compose missing edge-nginx service");
        await Assert.That(compose).DoesNotContain("${Parameters_")
            .Because("unresolved parameter placeholder leaked into compose");
        await Assert.That(compose).Contains(ContainerMountPaths.InterfoldSqliteData)
            .Because("API must bind-mount the sqlite data directory");
        await Assert.That(compose).Contains(OctoconEnvKeys.Persistence);
        await Assert.That(compose).Contains(PersistenceMode.Sqlite.ToWire());
        await Assert.That(compose).Contains(OctoconEnvKeys.SqliteConnection);
        await Assert.That(compose).Contains(
            $"{ContainerMountPaths.InterfoldSqliteData}/{ContainerMountPaths.InterfoldSqliteDbFileName}");
    }

    [Test]
    public async Task StackComesUpHealthyAndSecondBootstrapSkipsSecretUpsert()
    {
        var scratch = await dinD.CreateScratchAsync(
            nameof(StackComesUpHealthyAndSecondBootstrapSkipsSecretUpsert),
            TestConfigPaths.SqliteConfig);

        var first = await dinD.RunOnScratchAsync(
            scratch,
            $"{nameof(StackComesUpHealthyAndSecondBootstrapSkipsSecretUpsert)}-first",
            "bootstrap",
            "--skip-prereqs");
        await Assert.That(first.ExitCode).IsEqualTo(0)
            .Because($"first sqlite bootstrap failed: {first.Stderr}");

        var dbPath = $"{scratch.OutputDir}/{SqliteDbRelativePath}";
        var dbExists = await dinD.ExecAsync(["test", "-f", dbPath]);
        await Assert.That(dbExists.ExitCode).IsEqualTo(0L)
            .Because($"db-init must create {dbPath}");

        var ps = await dinD.ExecAsync(
            ["docker", "compose", "-f", $"{scratch.OutputDir}/docker-compose.yaml", "ps", "--format", "json"]);
        await Assert.That(ps.ExitCode).IsEqualTo(0L);
        await Assert.That(ps.Stdout).Contains("\"State\":\"running\"").Or.Contains("\"Health\":\"healthy\"")
            .Because("expected at least one healthy/running service after sqlite bootstrap");

        var second = await dinD.RunOnScratchAsync(
            scratch,
            $"{nameof(StackComesUpHealthyAndSecondBootstrapSkipsSecretUpsert)}-second",
            "bootstrap",
            "--skip-prereqs",
            "--print-phase-status");
        await Assert.That(second.ExitCode).IsEqualTo(0)
            .Because($"second sqlite bootstrap failed: {second.Stderr}");
        await Assert.That(second.Stderr).Contains("phase=secrets status=skipped")
            .Because("secrets phase should self-skip on a second run");
        await Assert.That(second.Stdout + second.Stderr).Contains("secrets already present")
            .Because("sqlite db-init must skip secret upsert when the pepper row exists");
    }
}
