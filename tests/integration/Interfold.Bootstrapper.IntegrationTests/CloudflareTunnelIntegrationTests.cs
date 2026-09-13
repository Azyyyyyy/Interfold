using System.Text;
using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Publish with Cloudflare Tunnel enabled against a local mock of the Cloudflare REST API.
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
[Explicit]
public sealed class CloudflareTunnelIntegrationTests(UbuntuDinDFixture dinD)
{
    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task PublishWithTunnelWritesTokenAndEmitsPrivateCloudflaredCompose()
    {
        var scratch = await dinD.CreateScratchAsync(
            nameof(PublishWithTunnelWritesTokenAndEmitsPrivateCloudflaredCompose),
            TestConfigPaths.CloudflareTunnelConfig);

        var port = 19878;
        var mockRoot = "/tmp/cf-api-mock";
        var setup = await dinD.ExecAsync(["sh", "-c", """
            set -e
            rm -rf __MOCK_ROOT__ /tmp/cf-tunnel-create.json
            mkdir -p __MOCK_ROOT__
            cat > __MOCK_ROOT__/server.py <<'PY'
            from http.server import BaseHTTPRequestHandler, HTTPServer
            import json

            def ok(handler, result):
                body = json.dumps({"success": True, "result": result, "errors": []}).encode()
                handler.send_response(200)
                handler.send_header("Content-Type", "application/json")
                handler.send_header("Content-Length", str(len(body)))
                handler.end_headers()
                handler.wfile.write(body)

            class H(BaseHTTPRequestHandler):
                def log_message(self, *args):
                    pass

                def _read(self):
                    n = int(self.headers.get("Content-Length", 0))
                    return self.rfile.read(n) if n else b""

                def do_GET(self):
                    path = self.path.split("?", 1)[0]
                    if path.startswith("/client/v4/zones"):
                        ok(self, [{"id": "zone-test", "name": "example.com", "account": {"id": "acct-test"}}])
                    elif "/cfd_tunnel/" in path and path.endswith("/token"):
                        ok(self, "connector-token-from-get")
                    elif path.startswith("/client/v4/accounts/") and "cfd_tunnel" in path:
                        ok(self, [])
                    elif "/dns_records" in path:
                        ok(self, [])
                    else:
                        self.send_error(404)

                def do_POST(self):
                    body = self._read()
                    path = self.path.split("?", 1)[0]
                    if path.endswith("/cfd_tunnel"):
                        open("/tmp/cf-tunnel-create.json", "wb").write(body)
                        ok(self, {"id": "tun-test", "name": "interfold-test", "token": "connector-token-jwt"})
                    elif path.endswith("/dns_records"):
                        open("/tmp/cf-dns-post.json", "wb").write(body)
                        ok(self, {"id": "dns-1"})
                    else:
                        self.send_error(404)

                def do_PUT(self):
                    body = self._read()
                    open("/tmp/cf-ingress-put.json", "wb").write(body)
                    ok(self, {})

            HTTPServer(("127.0.0.1", __PORT__), H).serve_forever()
            PY
            python3 __MOCK_ROOT__/server.py >/tmp/cf-api-mock.log 2>&1 &
            echo $! > /tmp/cf-api-mock.pid
            for i in $(seq 1 30); do
              python3 -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:__PORT__/client/v4/zones')" 2>/dev/null && exit 0
              sleep 0.2
            done
            echo "mock CF API failed to start" >&2
            cat /tmp/cf-api-mock.log >&2 || true
            exit 1
            """.Replace("__MOCK_ROOT__", mockRoot).Replace("__PORT__", port.ToString())]);
        await Assert.That(setup.ExitCode).IsEqualTo(0L).Because(setup.Stderr + setup.Stdout);

        try
        {
            var publish = await dinD.ExecAsync([
                "sh", "-c",
                $"INTERFOLD_CLOUDFLARE_API_BASE_URL=http://127.0.0.1:{port}/client/v4/ " +
                $"{DinDFixtureBase.BootstrapperMountPath}/interfold-bootstrap publish " +
                $"--config {scratch.ConfigPath} --output-dir {scratch.OutputDir} --non-interactive",
            ]);
            await Assert.That(publish.ExitCode).IsEqualTo(0L).Because(publish.Stderr + publish.Stdout);

            var token = await dinD.CopyOutAsTextAsync($"{scratch.OutputDir}/secrets/cloudflare-tunnel.token");
            await Assert.That(token.Trim()).IsEqualTo("connector-token-jwt");

            var state = await dinD.CopyOutAsTextAsync($"{scratch.OutputDir}/.cloudflare-tunnel.json");
            await Assert.That(state).Contains("tun-test");
            await Assert.That(state).Contains("acct-test");
            await Assert.That(state).Contains("zone-test");

            var compose = Encoding.UTF8.GetString(await dinD.CopyOutAsync($"{scratch.OutputDir}/docker-compose.yaml"));
            await Assert.That(compose).Contains("cloudflared");
            await Assert.That(compose).Contains("cloudflare/cloudflared");
            await Assert.That(compose).Contains("--token-file");
            await Assert.That(compose).DoesNotContain($"\"{scratch.Ports.EdgeHttp}:80\"")
                .Because("tunnel mode must not publish edge host ports");
            await Assert.That(compose).DoesNotContain($"\"{scratch.Ports.EdgeHttps}:443\"");

            var createBody = await dinD.CopyOutAsTextAsync("/tmp/cf-tunnel-create.json");
            await Assert.That(createBody).Contains("interfold-test");
            await Assert.That(createBody).Contains("cloudflare");
        }
        finally
        {
            await dinD.ExecAsync(["sh", "-c", "kill $(cat /tmp/cf-api-mock.pid) 2>/dev/null || true"]);
        }
    }

    [Test]
    public async Task PublishWithAccessWritesStateAndServiceToken()
    {
        var scratch = await dinD.CreateScratchAsync(
            nameof(PublishWithAccessWritesStateAndServiceToken),
            TestConfigPaths.CloudflareAccessConfig);

        var port = 19879;
        var mockRoot = "/tmp/cf-access-mock";
        var setup = await dinD.ExecAsync(["sh", "-c", """
            set -e
            rm -rf __MOCK_ROOT__
            mkdir -p __MOCK_ROOT__
            cat > __MOCK_ROOT__/server.py <<'PY'
            from http.server import BaseHTTPRequestHandler, HTTPServer
            import json

            def ok(handler, result):
                body = json.dumps({"success": True, "result": result, "errors": []}).encode()
                handler.send_response(200)
                handler.send_header("Content-Type", "application/json")
                handler.send_header("Content-Length", str(len(body)))
                handler.end_headers()
                handler.wfile.write(body)

            class H(BaseHTTPRequestHandler):
                def log_message(self, *args):
                    pass

                def _read(self):
                    n = int(self.headers.get("Content-Length", 0))
                    return self.rfile.read(n) if n else b""

                def do_GET(self):
                    path = self.path.split("?", 1)[0]
                    if path.startswith("/client/v4/zones"):
                        ok(self, [{"id": "zone-test", "name": "example.com", "account": {"id": "acct-test"}}])
                    elif "/cfd_tunnel/" in path and path.endswith("/token"):
                        ok(self, "connector-token-from-get")
                    elif path.startswith("/client/v4/accounts/") and "cfd_tunnel" in path:
                        ok(self, [])
                    elif path.endswith("/access/organizations"):
                        ok(self, {"name": "Interfold", "auth_domain": "team.cloudflareaccess.com"})
                    elif path.endswith("/access/identity_providers"):
                        ok(self, [])
                    elif path.endswith("/access/apps"):
                        ok(self, [])
                    elif "/access/apps/" in path and path.endswith("/policies"):
                        ok(self, [])
                    elif path.endswith("/access/service_tokens"):
                        ok(self, [])
                    elif "/dns_records" in path:
                        ok(self, [])
                    else:
                        self.send_error(404)

                def do_POST(self):
                    body = self._read()
                    path = self.path.split("?", 1)[0]
                    if path.endswith("/cfd_tunnel"):
                        ok(self, {"id": "tun-test", "name": "interfold-test", "token": "connector-token-jwt"})
                    elif path.endswith("/access/identity_providers"):
                        open("/tmp/cf-idp-post.json", "wb").write(body)
                        ok(self, {"id": "idp-test", "name": "interfold-google"})
                    elif path.endswith("/access/apps"):
                        open("/tmp/cf-app-post.json", "ab").write(body + b"\n")
                        domain = json.loads(body).get("domain", "api.example.com")
                        ok(self, {"id": "app-" + domain.replace("/", "-"), "domain": domain, "aud": "aud-primary"})
                    elif "/policies" in path:
                        open("/tmp/cf-policy-post.json", "ab").write(body + b"\n")
                        ok(self, {"id": "pol-1"})
                    elif path.endswith("/access/service_tokens"):
                        open("/tmp/cf-st-post.json", "wb").write(body)
                        ok(self, {"id": "st-1", "client_id": "cf-client", "client_secret": "cf-secret"})
                    elif path.endswith("/dns_records"):
                        ok(self, {"id": "dns-1"})
                    else:
                        self.send_error(404)

                def do_PUT(self):
                    body = self._read()
                    if "cfd_tunnel" in self.path:
                        ok(self, {})
                    else:
                        ok(self, {"id": "put-1", "aud": "aud-primary"})

                def do_DELETE(self):
                    ok(self, {})

            HTTPServer(("127.0.0.1", __PORT__), H).serve_forever()
            PY
            python3 __MOCK_ROOT__/server.py >/tmp/cf-access-mock.log 2>&1 &
            echo $! > /tmp/cf-access-mock.pid
            for i in $(seq 1 30); do
              python3 -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:__PORT__/client/v4/zones')" 2>/dev/null && exit 0
              sleep 0.2
            done
            echo "mock CF Access API failed to start" >&2
            cat /tmp/cf-access-mock.log >&2 || true
            exit 1
            """.Replace("__MOCK_ROOT__", mockRoot).Replace("__PORT__", port.ToString())]);
        await Assert.That(setup.ExitCode).IsEqualTo(0L).Because(setup.Stderr + setup.Stdout);

        try
        {
            var publish = await dinD.ExecAsync([
                "sh", "-c",
                $"INTERFOLD_CLOUDFLARE_API_BASE_URL=http://127.0.0.1:{port}/client/v4/ " +
                $"{DinDFixtureBase.BootstrapperMountPath}/interfold-bootstrap publish " +
                $"--config {scratch.ConfigPath} --output-dir {scratch.OutputDir} --non-interactive",
            ]);
            await Assert.That(publish.ExitCode).IsEqualTo(0L).Because(publish.Stderr + publish.Stdout);

            var accessState = await dinD.CopyOutAsTextAsync($"{scratch.OutputDir}/.cloudflare-access.json");
            await Assert.That(accessState).Contains("team.cloudflareaccess.com");
            await Assert.That(accessState).Contains("aud-primary");
            await Assert.That(accessState).Contains("idp-test");

            var serviceToken = await dinD.CopyOutAsTextAsync($"{scratch.OutputDir}/secrets/cloudflare-access-service.token");
            await Assert.That(serviceToken).Contains("cf-client");
            await Assert.That(serviceToken).Contains("cf-secret");

            var env = await dinD.CopyOutAsTextAsync($"{scratch.OutputDir}/.env");
            await Assert.That(env).Contains("CF_ACCESS_TEAM_DOMAIN");
            await Assert.That(env).Contains("team.cloudflareaccess.com");
            await Assert.That(env).Contains("aud-primary");

            var idp = await dinD.CopyOutAsTextAsync("/tmp/cf-idp-post.json");
            await Assert.That(idp).Contains("interfold-google");
            await Assert.That(idp).Contains("test-google-id");

            await Assert.That(publish.Stderr + publish.Stdout)
                .Contains("https://team.cloudflareaccess.com/cdn-cgi/access/callback");
        }
        finally
        {
            await dinD.ExecAsync(["sh", "-c", "kill $(cat /tmp/cf-access-mock.pid) 2>/dev/null || true"]);
        }
    }
}
