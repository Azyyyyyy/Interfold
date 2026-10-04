using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Argv-shape assertions for <see cref="UpdateImagesPhase"/>. Mirrors the pattern in
/// <see cref="BackupCommandBuildingTests"/> — pins the docker compose command layout so
/// a future refactor that swaps flag order or drops a service filter catches the
/// change at unit speed instead of only in DinD integration tests.
/// </summary>
public sealed class UpdateCommandBuildingTests
{


    [Test]
    public async Task ComposePsRequestsJsonFormat()
    {
        // The digest diff depends on `--format json` — the default text output isn't
        // parseable enough to reliably extract per-service image IDs.
        var args = UpdateImagesPhase.BuildComposePsJsonArgs(
            composeFile: "/srv/deploy/docker-compose.yaml");

        await Assert.That(args).IsEquivalentTo(new[]
        {
            "compose", "-f", "/srv/deploy/docker-compose.yaml", "ps", "-a", "--format", "json",
        });
    }

    [Test]
    public async Task ImageInspectIdArgsPinFormat()
    {
        var args = UpdateImagesPhase.BuildImageInspectIdArgs("interfold-api:latest");

        await Assert.That(args).IsEquivalentTo(new[]
        {
            "image", "inspect", "--format", "{{.Id}}", "interfold-api:latest",
        });
    }

    [Test]
    public async Task ContainerImageFieldsArgsInspectAllIds()
    {
        var args = UpdateImagesPhase.BuildContainerImageFieldsArgs(["abc", "def"]);

        await Assert.That(args).IsEquivalentTo(new[]
        {
            "inspect", "--format", "{{.Image}}\t{{.Config.Image}}", "abc", "def",
        });
    }

    [Test]
    public async Task ResolveServiceWhitelistCliBeatsConfig()
    {
        // CLI --service wins over config.update.services. The operator's ad-hoc override
        // is always the more specific intent.
        var options = TestSupport.MakeOptions(updateServices: ["interfold-api"]);
        var config = new BootstrapConfig { Deployment = { Update = { Services = ["interfold-api", "edge-nginx"] } } };

        var resolved = UpdateImagesPhase.ResolveServiceWhitelist(options, config);

        await Assert.That(resolved).IsEquivalentTo(new[] { "interfold-api" });
    }

    [Test]
    public async Task ResolveServiceWhitelistFallsBackToConfig()
    {
        // No CLI → use the persistent config value.
        var options = TestSupport.MakeOptions(updateServices: null);
        var config = new BootstrapConfig { Deployment = { Update = { Services = ["interfold-api"] } } };

        var resolved = UpdateImagesPhase.ResolveServiceWhitelist(options, config);

        await Assert.That(resolved).IsEquivalentTo(new[] { "interfold-api" });
    }

    [Test]
    public async Task ResolveServiceWhitelistBothEmptyMeansEveryService()
    {
        // The empty sentinel propagates through — UpdateImagesPhase interprets an empty
        // result as "pass no service names to docker compose", which compose reads as
        // "every service in the file".
        var options = TestSupport.MakeOptions(updateServices: null);
        var config = new BootstrapConfig();

        var resolved = UpdateImagesPhase.ResolveServiceWhitelist(options, config);

        await Assert.That(resolved.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ResolveServiceWhitelistCanonicalizesOctoconWeb()
    {
        var options = TestSupport.MakeOptions(updateServices: ["octocon-web"]);
        var config = new BootstrapConfig { Deployment = { Update = { Services = ["octocon-web"] } } };

        var fromCli = UpdateImagesPhase.ResolveServiceWhitelist(options, config);
        var fromConfig = UpdateImagesPhase.ResolveServiceWhitelist(
            TestSupport.MakeOptions(updateServices: null), config);

        await Assert.That(fromCli).IsEquivalentTo(new[] { "interfold-web" });
        await Assert.That(fromConfig).IsEquivalentTo(new[] { "interfold-web" });
    }

    [Test]
    public async Task DiffDigestsReturnsChangedServicesOnly()
    {
        // Only the services whose image ID changed appear in the diff. Unchanged
        // services stay off the recreate list, which is the whole point of the diff.
        var before = new Dictionary<string, string>
        {
            ["interfold-web"] = "sha256:aaa",
            ["edge-nginx"] = "sha256:bbb",
            ["interfold-api"] = "sha256:ccc",
        };
        var after = new Dictionary<string, string>
        {
            ["interfold-web"] = "sha256:aaa",
            ["edge-nginx"] = "sha256:bbb",
            ["interfold-api"] = "sha256:ddd",
        };

        var changed = UpdateImagesPhase.DiffDigests(before, after);
        await Assert.That(changed).IsEquivalentTo(new[] { "interfold-api" });
    }

    [Test]
    public async Task DiffDigestsHandlesNewService()
    {
        var before = new Dictionary<string, string> { ["interfold-web"] = "sha256:aaa" };
        var after = new Dictionary<string, string>
        {
            ["interfold-web"] = "sha256:aaa",
            ["new-svc"] = "sha256:new",
        };

        var changed = UpdateImagesPhase.DiffDigests(before, after);
        await Assert.That(changed).Contains("new-svc");
    }

    [Test]
    public async Task DiffDigestsHandlesRemovedService()
    {
        var before = new Dictionary<string, string>
        {
            ["interfold-web"] = "sha256:aaa",
            ["dropped"] = "sha256:x",
        };
        var after = new Dictionary<string, string> { ["interfold-web"] = "sha256:aaa" };

        var changed = UpdateImagesPhase.DiffDigests(before, after);
        await Assert.That(changed).Contains("dropped");
    }

    [Test]
    public async Task DiffDigestsAllSameReturnsEmpty()
    {
        // The "no-op" case: every image ID matches. UpdateImagesPhase short-circuits
        // here and skips the recreate + health check + downtime.
        var before = new Dictionary<string, string>
        {
            ["interfold-web"] = "sha256:aaa",
            ["edge-nginx"] = "sha256:bbb",
        };
        var after = new Dictionary<string, string>
        {
            ["interfold-web"] = "sha256:aaa",
            ["edge-nginx"] = "sha256:bbb",
        };

        var changed = UpdateImagesPhase.DiffDigests(before, after);
        await Assert.That(changed.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ResolveHealthProbesSqliteIsApiOnly()
    {
        var config = new BootstrapConfig();

        var probes = UpdateImagesPhase.ResolveHealthProbes(config);

        await Assert.That(probes).IsEquivalentTo(new[] { UpdateImagesPhase.UpdateHealthProbe.Api });
    }

    [Test]
    public async Task ResolveHealthFailureLogServicesOmitsDatastoresForSqlite()
    {
        var sqlite = UpdateImagesPhase.ResolveHealthFailureLogServices(new BootstrapConfig());

        await Assert.That(sqlite).DoesNotContain("msg-db");
        await Assert.That(sqlite).DoesNotContain("scylla");
        await Assert.That(sqlite).Contains("interfold-api");
    }
}

