using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class ComposeRegistryPullTests
{
    [Test]
    public async Task PullsRegistryImagesAndLeavesLocalTags()
    {
        var compose = """
            services:
              interfold-api:
                image: "ghcr.io/azyyyyyy/interfold-api:bleeding-edge"
              interfold-web:
                image: "ghcr.io/azyyyyyy/interfold-wasm:latest"
              edge-nginx:
                image: "nginx:1.27-alpine"
              cassandra:
                image: "ghcr.io/example/cassandra:local"
                pull_policy: never
            volumes:
              data:
            """;

        var services = ComposeRegistryPull.ServicesToPull(compose);

        await Assert.That(services).IsEquivalentTo(["interfold-api", "interfold-web"]);
    }
}
