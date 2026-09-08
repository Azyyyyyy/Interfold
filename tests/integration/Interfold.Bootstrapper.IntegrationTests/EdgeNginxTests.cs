using System.Text;
using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using Interfold.Bootstrapper.IntegrationTests.TestServices;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Integration tests for always-on <c>edge-nginx</c>. The bootstrapper must emit compose where
/// only edge publishes host ports, upstreams sit on isolated Docker networks, and templates mount
/// correctly for private-CA HTTPS and plaintext HTTP modes.
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
[Explicit]
public class EdgeNginxTests(UbuntuDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task PublishWithEdgeEmitsCertAndTemplateBindMounts()
    {
        var (scratch, _) = await dinD.PublishAsync(
            nameof(PublishWithEdgeEmitsCertAndTemplateBindMounts), TestConfigPaths.EdgeConfig);

        var envBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/.env");
        var env = DotEnvParser.ParseEnv(envBytes);

        var edgeBindMounts = env
            .Where(kv => kv.Key.StartsWith("EDGE_NGINX_BINDMOUNT", StringComparison.OrdinalIgnoreCase))
            .ToList();
        await Assert.That(edgeBindMounts.Count).IsGreaterThanOrEqualTo(3)
            .Because("expected at least three edge-nginx bind mounts " +
                     "(template, proxy_params, certs) " +
                     $"in the .env; got keys: {string.Join(", ", env.Keys)}");

        foreach (var (key, value) in edgeBindMounts)
        {
            await Assert.That(value).IsNotEmpty()
                .Because($"{key} should not be blank — PublishPhase failed to substitute the bind-mount source");
            await Assert.That(value).StartsWith("/")
                .Because($"{key}={value} must be an absolute host path");
        }

        var composeBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/docker-compose.yaml");
        var compose = Encoding.UTF8.GetString(composeBytes);

        await Assert.That(compose).Contains("edge-nginx")
            .Because("edge-nginx is always emitted");
        await Assert.That(compose).Contains("octocon-web")
            .Because("octocon-web ships when includeWeb=true");
        await Assert.That(compose).Contains("/etc/nginx/templates/default.conf.template")
            .Because("edge-nginx must bind-mount the nginx envsubst template");
        await Assert.That(compose).Contains("/etc/nginx/proxy_params_interfold.conf")
            .Because("edge-nginx must bind-mount proxy_params");
        await Assert.That(compose).DoesNotContain("/etc/nginx/cloudflare-ips.conf")
            .Because("Cloudflare Tunnel replaced the orange-cloud IP allowlist mount");
        await Assert.That(compose).Contains("/certs")
            .Because("edge-nginx must bind-mount the certs directory at /certs");
        await Assert.That(compose).Contains("NGINX_SERVER_NAME")
            .Because("edge-nginx must receive NGINX_SERVER_NAME");
        await Assert.That(compose).Contains("NGINX_SSL_CERT_FILE")
            .Because("edge-nginx must receive NGINX_SSL_CERT_FILE");
        await Assert.That(compose).Contains("NGINX_SSL_KEY_FILE")
            .Because("edge-nginx must receive NGINX_SSL_KEY_FILE");
        await Assert.That(compose).Contains("NGINX_HTTPS_PORT_SUFFIX")
            .Because("edge-nginx must receive NGINX_HTTPS_PORT_SUFFIX for non-443 redirects");
        await Assert.That(compose).Contains($"\"{scratch.Ports.EdgeHttp}:80\"")
            .Because($"compose must publish edgeHttp ({scratch.Ports.EdgeHttp}) onto :80");
        await Assert.That(compose).Contains($"\"{scratch.Ports.EdgeHttps}:443\"")
            .Because($"compose must publish edgeHttps ({scratch.Ports.EdgeHttps}) onto :443");

        await AssertIsolationAsync(compose, scratch.Ports);
    }

    [Test]
    public async Task PublishWithPlaintextEdgeOmitsHttpsPortAndCertsBindMount()
    {
        var (scratch, _) = await dinD.PublishAsync(
            nameof(PublishWithPlaintextEdgeOmitsHttpsPortAndCertsBindMount), TestConfigPaths.EdgeHttpConfig);

        var composeBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/docker-compose.yaml");
        var compose = Encoding.UTF8.GetString(composeBytes);

        await Assert.That(compose).Contains($"\"{scratch.Ports.EdgeHttp}:80\"")
            .Because("plaintext edge must publish edgeHttp");
        await Assert.That(compose).DoesNotContain($"\"{scratch.Ports.EdgeHttps}:443\"")
            .Because("plaintext edge must not publish edgeHttps");

        var envBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/.env");
        var env = DotEnvParser.ParseEnv(envBytes);
        var certMounts = env
            .Where(kv => kv.Key.StartsWith("EDGE_NGINX_BINDMOUNT", StringComparison.OrdinalIgnoreCase)
                         && kv.Value.Contains("/certs", StringComparison.Ordinal))
            .ToList();
        await Assert.That(certMounts).IsEmpty()
            .Because("tlsMode=none must not bind-mount certs into edge-nginx");

        await AssertIsolationAsync(compose, scratch.Ports);
    }

    [Test]
    public async Task EdgeContainerServesHttpsAfterComposeUp()
    {
        var (scratch, _) = await dinD.PublishAsync(
            nameof(EdgeContainerServesHttpsAfterComposeUp), TestConfigPaths.EdgeConfig);

        var up = await dinD.ExecAsync(
        [
            "sh", "-c",
            $"docker compose -f {scratch.OutputDir}/docker-compose.yaml up -d --no-deps edge-nginx 2>&1"
        ]);
        await Assert.That(up.ExitCode).IsEqualTo(0)
            .Because($"`docker compose up -d edge-nginx` failed: {up.Stdout}{up.Stderr}");

        var containerProbe = await dinD.ExecAsync(
        [
            "sh", "-c",
            $"docker compose -f {scratch.OutputDir}/docker-compose.yaml ps -q edge-nginx"
        ]);
        var containerId = containerProbe.Stdout.Trim();
        await Assert.That(containerId).IsNotEmpty()
            .Because("expected `docker compose ps -q edge-nginx` to print the running container id");

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        var lastStatus = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            var inspect = await dinD.ExecAsync(
            [
                "sh", "-c",
                $"docker inspect --format '{{{{.State.Status}}}}' {containerId}"
            ]);
            lastStatus = inspect.Stdout.Trim();
            if (string.Equals(lastStatus, "running", StringComparison.Ordinal)) break;
            if (string.Equals(lastStatus, "exited", StringComparison.Ordinal)
                || string.Equals(lastStatus, "dead", StringComparison.Ordinal))
            {
                var logs = await dinD.ExecAsync(["docker", "logs", "--tail", "100", containerId]);
                throw new InvalidOperationException(
                    $"edge-nginx exited early (status={lastStatus}):\n{logs.Stdout}\n{logs.Stderr}");
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        await Assert.That(lastStatus).IsEqualTo("running")
            .Because($"edge-nginx never reached running (last status: '{lastStatus}')");

        await WaitForNginxHttpHealthAsync(containerId);

        var renderedConf = await dinD.ExecAsync(
            ["docker", "exec", containerId, "cat", "/etc/nginx/conf.d/default.conf"]);
        await Assert.That(renderedConf.ExitCode).IsEqualTo(0)
            .Because($"could not read rendered nginx config: {renderedConf.Stderr}");
        await Assert.That(renderedConf.Stdout).Contains("/certs/leaf.crt")
            .Because("envsubst should have substituted NGINX_SSL_CERT_FILE into ssl_certificate");
        await Assert.That(renderedConf.Stdout).Contains("/certs/leaf.key")
            .Because("envsubst should have substituted NGINX_SSL_KEY_FILE into ssl_certificate_key");
        await Assert.That(renderedConf.Stdout).Contains("web.test.local")
            .Because("envsubst should have substituted NGINX_SERVER_NAME from the fixture's hosts[0]");

        var expectedRedirect = scratch.Ports.EdgeHttps == 443
            ? null
            : $"return 301 https://$host:{scratch.Ports.EdgeHttps}$request_uri;";
        if (expectedRedirect is not null)
        {
            await Assert.That(renderedConf.Stdout).Contains(expectedRedirect)
                .Because($"the rendered redirect must embed the operator's edgeHttps host port " +
                         $"({scratch.Ports.EdgeHttps}); got conf body:\n{renderedConf.Stdout}");
        }

        var redirectProbe = await dinD.ExecAsync(
        [
            "docker", "exec", containerId,
            "wget", "-qSO-", "http://127.0.0.1/"
        ]);
        var redirectHeaders = redirectProbe.Stdout + redirectProbe.Stderr;
        var expectedLocationFragment = scratch.Ports.EdgeHttps == 443
            ? "Location: https://localhost/"
            : $"Location: https://localhost:{scratch.Ports.EdgeHttps}/";
        var altLocationFragment = scratch.Ports.EdgeHttps == 443
            ? "Location: https://127.0.0.1/"
            : $"Location: https://127.0.0.1:{scratch.Ports.EdgeHttps}/";
        await Assert.That(
                redirectHeaders.Contains(expectedLocationFragment, StringComparison.OrdinalIgnoreCase)
                || redirectHeaders.Contains(altLocationFragment, StringComparison.OrdinalIgnoreCase))
            .IsTrue()
            .Because($"the 301 Location header must reference the operator-mapped edgeHttps port " +
                     $"({scratch.Ports.EdgeHttps}); got:\n{redirectHeaders}");

        var tlsProbe = await dinD.ExecAsync(
        [
            "docker", "exec", containerId,
            "wget", "--no-check-certificate", "-qO-", "https://127.0.0.1/nginx-health"
        ]);
        await Assert.That(tlsProbe.ExitCode).IsEqualTo(0)
            .Because($"HTTPS probe against edge-nginx failed: stdout='{tlsProbe.Stdout}' stderr='{tlsProbe.Stderr}'");
        await Assert.That(tlsProbe.Stdout.Trim()).IsEqualTo("ok")
            .Because($"expected 'ok' from https://127.0.0.1/nginx-health, got '{tlsProbe.Stdout.Trim()}'");
    }

    private async Task WaitForNginxHttpHealthAsync(string containerId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        var lastOutput = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            var probe = await dinD.ExecAsync(
            [
                "docker", "exec", containerId,
                "wget", "-qO-", "http://127.0.0.1/nginx-health"
            ]);
            lastOutput = $"{probe.Stdout}{probe.Stderr}";
            if (probe.ExitCode == 0 && probe.Stdout.Trim() == "ok") return;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        var logs = await dinD.ExecAsync(["docker", "logs", "--tail", "100", containerId]);
        throw new InvalidOperationException(
            $"edge-nginx never served HTTP /nginx-health within 60s (last probe: {lastOutput}). " +
            $"Logs:\n{logs.Stdout}\n{logs.Stderr}");
    }

    [Test]
    public async Task PublishWithEdgeLeavesEnvFilledWithAbsolutePaths()
    {
        var (scratch, _) = await dinD.PublishAsync(
            nameof(PublishWithEdgeLeavesEnvFilledWithAbsolutePaths), TestConfigPaths.EdgeConfig);

        var envBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/.env");
        var rawText = Encoding.UTF8.GetString(envBytes);

        foreach (var line in rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (!trimmed.StartsWith("EDGE_NGINX", StringComparison.OrdinalIgnoreCase)) continue;
            await Assert.That(trimmed.EndsWith('=')).IsFalse()
                .Because($"edge-nginx env line has a blank value (compose up would fail): {trimmed}");
        }
    }

    private static async Task AssertIsolationAsync(string compose, DinDPortAllocation ports)
    {
        await Assert.That(compose).Contains(ComposeNetworks.EdgeApi);
        await Assert.That(compose).Contains(ComposeNetworks.EdgeWeb);
        await Assert.That(compose).DoesNotContain($"\"{ports.EdgeHttp}:5432\"");
        await Assert.That(compose).DoesNotContain($"\"{ports.EdgeHttp}:9042\"");
        await Assert.That(compose).DoesNotContain($"\"{ports.EdgeHttps}:5432\"");
        await Assert.That(compose).DoesNotContain($"\"{ports.EdgeHttps}:9042\"");
    }
}
