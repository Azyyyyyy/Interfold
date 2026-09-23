using System.Net;
using System.Text;
using System.Text.Json;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class CloudflareTunnelClientTests
{
    [Test]
    public async Task EnumerateZoneCandidatesWalksApex()
    {
        var candidates = CloudflareTunnelClient.EnumerateZoneCandidates("api.eu.example.com").ToArray();
        await Assert.That(candidates).Contains("api.eu.example.com");
        await Assert.That(candidates).Contains("eu.example.com");
        await Assert.That(candidates).Contains("example.com");
    }

    [Test]
    public async Task ResolvePublicHostnamesPathModeUsesDnsHosts()
    {
        var config = TestSupport.MakeConfig();
        config.Edge.Hosts = ["api.example.com", "192.168.1.1", "10.0.0.0/8"];
        var hosts = CloudflareTunnelPhase.ResolvePublicHostnames(config);
        await Assert.That(hosts.Count).IsEqualTo(1);
        await Assert.That(hosts[0]).IsEqualTo("api.example.com");
    }

    [Test]
    public async Task ResolvePublicHostnamesSubdomainMode()
    {
        var config = TestSupport.MakeConfig();
        config.Deployment.IncludeWeb = true;
        config.Edge.Routing.Mode = EdgeRoutingMode.Subdomain;
        config.Edge.Routing.ApiHost = "api.example.com";
        config.Edge.Routing.WebHost = "app.example.com";
        var hosts = CloudflareTunnelPhase.ResolvePublicHostnames(config);
        await Assert.That(hosts.Count).IsEqualTo(2);
        await Assert.That(hosts).Contains("api.example.com");
        await Assert.That(hosts).Contains("app.example.com");
    }

    [Test]
    public async Task BuildPublicReadyUrlUsesHealthPath()
    {
        await Assert.That(CloudflareTunnelClient.BuildPublicReadyUrl("api.example.com."))
            .IsEqualTo("https://api.example.com/health/ready");
    }

    [Test]
    public async Task ResolveZoneAndAccountUsesFirstMatchingCandidate()
    {
        var handler = new StubCloudflareHandler();
        handler.OnGet = (path, query) =>
        {
            if (path == "zones" && query.Contains("name=example.com", StringComparison.Ordinal))
            {
                return Json("""{"success":true,"result":[{"id":"zone-1","name":"example.com","account":{"id":"acct-1"}}],"errors":[]}""");
            }

            return Json("""{"success":true,"result":[],"errors":[]}""");
        };

        using var client = CreateClient(handler);
        var zone = await client.ResolveZoneAndAccountAsync("api.example.com", CancellationToken.None);
        await Assert.That(zone.ZoneId).IsEqualTo("zone-1");
        await Assert.That(zone.AccountId).IsEqualTo("acct-1");
        await Assert.That(zone.ZoneName).IsEqualTo("example.com");
    }

    [Test]
    public async Task ResolveZoneAndAccountCreatesApexWhenMissing()
    {
        string? postBody = null;
        var handler = new StubCloudflareHandler();
        handler.OnGet = (path, _) =>
        {
            if (path == "zones")
                return Json("""{"success":true,"result":[],"errors":[]}""");
            if (path == "accounts")
                return Json("""{"success":true,"result":[{"id":"acct-1","name":"Interfold"}],"errors":[]}""");
            return Fail();
        };
        handler.OnPost = (path, body) =>
        {
            postBody = body;
            return Json("""{"success":true,"result":{"id":"zone-new","name":"test.api","account":{"id":"acct-1"},"name_servers":["ns1.cloudflare.com","ns2.cloudflare.com"]},"errors":[]}""");
        };

        using var client = CreateClient(handler);
        var zone = await client.ResolveZoneAndAccountAsync("test.api", CancellationToken.None);
        await Assert.That(zone.ZoneId).IsEqualTo("zone-new");
        await Assert.That(zone.AccountId).IsEqualTo("acct-1");
        await Assert.That(zone.ZoneName).IsEqualTo("test.api");
        await Assert.That(zone.Created).IsTrue();
        await Assert.That(zone.NameServers).IsNotNull();
        await Assert.That(zone.NameServers!).Contains("ns1.cloudflare.com");

        using var doc = JsonDocument.Parse(postBody!);
        await Assert.That(doc.RootElement.GetProperty("name").GetString()).IsEqualTo("test.api");
        await Assert.That(doc.RootElement.GetProperty("type").GetString()).IsEqualTo("full");
        await Assert.That(doc.RootElement.GetProperty("jump_start").GetBoolean()).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("account").GetProperty("id").GetString()).IsEqualTo("acct-1");
    }

    [Test]
    public async Task ResolveZoneAndAccountRefusesToPickAmongAccounts()
    {
        var handler = new StubCloudflareHandler();
        handler.OnGet = (path, _) =>
        {
            if (path == "zones")
                return Json("""{"success":true,"result":[],"errors":[]}""");
            if (path == "accounts")
            {
                return Json(
                    """{"success":true,"result":[{"id":"acct-1","name":"One"},{"id":"acct-2","name":"Two"}],"errors":[]}""");
            }

            return Fail();
        };

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ResolveZoneAndAccountAsync("api.example.com", CancellationToken.None));
        await Assert.That(ex!.Message).Contains("multiple accounts");
        await Assert.That(handler.PostCount).IsEqualTo(0);
    }

    [Test]
    public async Task ResolveZoneAndAccountRejectsSingleLabelHostname()
    {
        var handler = new StubCloudflareHandler();
        handler.OnGet = (_, _) => Json("""{"success":true,"result":[],"errors":[]}""");

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ResolveZoneAndAccountAsync("localhost", CancellationToken.None));
        await Assert.That(ex!.Message).Contains("not a domain");
        await Assert.That(handler.PostCount).IsEqualTo(0);
    }

    [Test]
    public async Task EnsureTunnelCreatesWhenMissing()
    {
        string? postBody = null;
        var handler = new StubCloudflareHandler();
        handler.OnGet = (path, _) =>
        {
            if (path.Contains("cfd_tunnel", StringComparison.Ordinal))
                return Json("""{"success":true,"result":[],"errors":[]}""");
            return Fail();
        };
        handler.OnPost = (_, body) =>
        {
            postBody = body;
            return Json("""{"success":true,"result":{"id":"tun-1","name":"interfold","token":"connector-jwt"},"errors":[]}""");
        };

        using var client = CreateClient(handler);
        var creds = await client.EnsureTunnelAsync("acct-1", "interfold", CancellationToken.None);
        await Assert.That(creds.TunnelId).IsEqualTo("tun-1");
        await Assert.That(creds.ConnectorToken).IsEqualTo("connector-jwt");

        using var doc = JsonDocument.Parse(postBody!);
        await Assert.That(doc.RootElement.GetProperty("name").GetString()).IsEqualTo("interfold");
        await Assert.That(doc.RootElement.GetProperty("config_src").GetString()).IsEqualTo("cloudflare");
    }

    [Test]
    public async Task EnsureTunnelReusesExistingAndFetchesToken()
    {
        var handler = new StubCloudflareHandler();
        handler.OnGet = (path, _) =>
        {
            if (path.EndsWith("/token", StringComparison.Ordinal))
                return Json("""{"success":true,"result":"fetched-token","errors":[]}""");
            if (path.Contains("cfd_tunnel", StringComparison.Ordinal))
                return Json("""{"success":true,"result":[{"id":"tun-existing","name":"interfold"}],"errors":[]}""");
            return Fail();
        };

        using var client = CreateClient(handler);
        var creds = await client.EnsureTunnelAsync("acct-1", "interfold", CancellationToken.None);
        await Assert.That(creds.TunnelId).IsEqualTo("tun-existing");
        await Assert.That(creds.ConnectorToken).IsEqualTo("fetched-token");
        await Assert.That(handler.PostCount).IsEqualTo(0);
    }

    [Test]
    public async Task PutIngressSendsHostnamesAndCatchAll()
    {
        string? putBody = null;
        var handler = new StubCloudflareHandler();
        handler.OnPut = (_, body) =>
        {
            putBody = body;
            return Json("""{"success":true,"result":{},"errors":[]}""");
        };

        using var client = CreateClient(handler);
        await client.PutIngressAsync(
            "acct-1",
            "tun-1",
            ["api.example.com", "app.example.com"],
            CloudflareTunnelPhase.OriginService,
            CancellationToken.None);

        using var doc = JsonDocument.Parse(putBody!);
        var ingress = doc.RootElement.GetProperty("config").GetProperty("ingress");
        await Assert.That(ingress.GetArrayLength()).IsEqualTo(3);
        await Assert.That(ingress[0].GetProperty("hostname").GetString()).IsEqualTo("api.example.com");
        await Assert.That(ingress[0].GetProperty("service").GetString())
            .IsEqualTo("http://edge-nginx:80");
        await Assert.That(ingress[2].GetProperty("service").GetString()).IsEqualTo("http_status:404");
    }

    [Test]
    public async Task UpsertDnsCnamePostsWhenMissing()
    {
        string? postBody = null;
        var handler = new StubCloudflareHandler();
        handler.OnGet = (path, _) =>
        {
            if (path.Contains("dns_records", StringComparison.Ordinal))
                return Json("""{"success":true,"result":[],"errors":[]}""");
            return Fail();
        };
        handler.OnPost = (_, body) =>
        {
            postBody = body;
            return Json("""{"success":true,"result":{"id":"dns-1"},"errors":[]}""");
        };

        using var client = CreateClient(handler);
        await client.UpsertDnsCnameAsync("zone-1", "api.example.com", "tun-1", CancellationToken.None);

        using var doc = JsonDocument.Parse(postBody!);
        await Assert.That(doc.RootElement.GetProperty("type").GetString()).IsEqualTo("CNAME");
        await Assert.That(doc.RootElement.GetProperty("name").GetString()).IsEqualTo("api.example.com");
        await Assert.That(doc.RootElement.GetProperty("content").GetString())
            .IsEqualTo("tun-1.cfargotunnel.com");
        await Assert.That(doc.RootElement.GetProperty("proxied").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task UpsertDnsCnamePatchesWhenPresent()
    {
        string? patchBody = null;
        var handler = new StubCloudflareHandler();
        handler.OnGet = (path, _) =>
        {
            if (path.Contains("dns_records", StringComparison.Ordinal))
                return Json("""{"success":true,"result":[{"id":"dns-existing"}],"errors":[]}""");
            return Fail();
        };
        handler.OnPatch = (_, body) =>
        {
            patchBody = body;
            return Json("""{"success":true,"result":{"id":"dns-existing"},"errors":[]}""");
        };

        using var client = CreateClient(handler);
        await client.UpsertDnsCnameAsync("zone-1", "api.example.com", "tun-1", CancellationToken.None);

        await Assert.That(handler.PostCount).IsEqualTo(0);
        using var doc = JsonDocument.Parse(patchBody!);
        await Assert.That(doc.RootElement.GetProperty("content").GetString())
            .IsEqualTo("tun-1.cfargotunnel.com");
    }

    [Test]
    public async Task GetOrganizationReadsAuthDomain()
    {
        var handler = new StubCloudflareHandler();
        handler.OnGet = (path, _) =>
        {
            if (path.EndsWith("/access/organizations", StringComparison.Ordinal))
                return Json("""{"success":true,"result":{"name":"Interfold","auth_domain":"team.cloudflareaccess.com"},"errors":[]}""");
            return Fail();
        };

        using var client = CreateClient(handler);
        var org = await client.GetOrganizationAsync("acct-1", CancellationToken.None);
        await Assert.That(org.AuthDomain).IsEqualTo("team.cloudflareaccess.com");
        await Assert.That(CloudflareTunnelClient.GoogleAccessCallbackUri(org.AuthDomain))
            .IsEqualTo("https://team.cloudflareaccess.com/cdn-cgi/access/callback");
        await Assert.That(CloudflareTunnelClient.AccessTeamOrigin(org.AuthDomain))
            .IsEqualTo("https://team.cloudflareaccess.com");
    }

    [Test]
    public async Task EnsureGoogleIdentityProviderCreatesWhenMissing()
    {
        string? postBody = null;
        var handler = new StubCloudflareHandler();
        handler.OnGet = (path, _) =>
        {
            if (path.Contains("identity_providers", StringComparison.Ordinal))
                return Json("""{"success":true,"result":[],"errors":[]}""");
            return Fail();
        };
        handler.OnPost = (_, body) =>
        {
            postBody = body;
            return Json("""{"success":true,"result":{"id":"idp-1","name":"interfold-google"},"errors":[]}""");
        };

        using var client = CreateClient(handler);
        var id = await client.EnsureGoogleIdentityProviderAsync("acct-1", "cid", "csecret", CancellationToken.None);
        await Assert.That(id).IsEqualTo("idp-1");
        using var doc = JsonDocument.Parse(postBody!);
        await Assert.That(doc.RootElement.GetProperty("type").GetString()).IsEqualTo("google");
        await Assert.That(doc.RootElement.GetProperty("name").GetString()).IsEqualTo("interfold-google");
    }

    [Test]
    public async Task EnsureSelfHostedAppCreatesWhenMissing()
    {
        string? postBody = null;
        var handler = new StubCloudflareHandler();
        handler.OnGet = (path, _) =>
        {
            if (path.EndsWith("/access/apps", StringComparison.Ordinal))
                return Json("""{"success":true,"result":[],"errors":[]}""");
            return Fail();
        };
        handler.OnPost = (_, body) =>
        {
            postBody = body;
            return Json("""{"success":true,"result":{"id":"app-1","domain":"api.example.com","aud":"aud-1"},"errors":[]}""");
        };

        using var client = CreateClient(handler);
        var app = await client.EnsureSelfHostedAppAsync("acct-1", "api.example.com", "idp-1", CancellationToken.None);
        await Assert.That(app.Aud).IsEqualTo("aud-1");
        using var doc = JsonDocument.Parse(postBody!);
        await Assert.That(doc.RootElement.GetProperty("domain").GetString()).IsEqualTo("api.example.com");
        await Assert.That(doc.RootElement.GetProperty("type").GetString()).IsEqualTo("self_hosted");
        await Assert.That(doc.RootElement.GetProperty("options_preflight_bypass").GetBoolean()).IsFalse();

        await client.EnsureSelfHostedAppAsync(
            "acct-1", "api.example.com", "idp-1", CancellationToken.None, optionsPreflightBypass: true);
        using var bypass = JsonDocument.Parse(postBody!);
        await Assert.That(bypass.RootElement.GetProperty("options_preflight_bypass").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task ReplaceAppPoliciesDeletesThenCreates()
    {
        string? created = null;
        var handler = new StubCloudflareHandler();
        handler.OnGet = (path, _) =>
        {
            if (path.Contains("/policies", StringComparison.Ordinal))
                return Json("""{"success":true,"result":[{"id":"pol-old"}],"errors":[]}""");
            return Fail();
        };
        handler.OnDelete = (_, _) => Json("""{"success":true,"result":{},"errors":[]}""");
        handler.OnPost = (_, body) =>
        {
            created = body;
            return Json("""{"success":true,"result":{"id":"pol-new"},"errors":[]}""");
        };

        using var client = CreateClient(handler);
        var policy = CloudflareAccessPhase.BuildAllowPolicy(
            ["ops@example.com"],
            ["example.com"],
            "idp-1");
        await client.ReplaceAppPoliciesAsync("acct-1", "app-1", [policy], CancellationToken.None);

        await Assert.That(handler.DeleteCount).IsEqualTo(1);
        using var doc = JsonDocument.Parse(created!);
        await Assert.That(doc.RootElement.GetProperty("decision").GetString()).IsEqualTo("allow");
        await Assert.That(doc.RootElement.GetProperty("include").GetArrayLength()).IsEqualTo(2);
        await Assert.That(doc.RootElement.GetProperty("require")[0].GetProperty("login_method").GetProperty("id").GetString())
            .IsEqualTo("idp-1");
    }

    [Test]
    public async Task EnsureServiceTokenCreatesWhenMissing()
    {
        var handler = new StubCloudflareHandler();
        handler.OnGet = (path, _) =>
        {
            if (path.Contains("service_tokens", StringComparison.Ordinal))
                return Json("""{"success":true,"result":[],"errors":[]}""");
            return Fail();
        };
        handler.OnPost = (_, _) => Json(
            """{"success":true,"result":{"id":"st-1","client_id":"cid","client_secret":"csec"},"errors":[]}""");

        using var client = CreateClient(handler);
        var token = await client.EnsureServiceTokenAsync("acct-1", "interfold-bootstrap", null, null, CancellationToken.None);
        await Assert.That(token.ClientId).IsEqualTo("cid");
        await Assert.That(token.ClientSecret).IsEqualTo("csec");
    }

    private static CloudflareTunnelClient CreateClient(HttpMessageHandler handler)
        => new(new HttpClient(handler)
        {
            BaseAddress = new Uri(CloudflareTunnelClient.DefaultApiBaseUrl),
        });

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Fail()
        => new(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{"success":false,"errors":[{"code":7003,"message":"not found"}],"result":null}""",
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class StubCloudflareHandler : HttpMessageHandler
    {
        public Func<string, string, HttpResponseMessage>? OnGet { get; set; }
        public Func<string, string, HttpResponseMessage>? OnPost { get; set; }
        public Func<string, string, HttpResponseMessage>? OnPut { get; set; }
        public Func<string, string, HttpResponseMessage>? OnPatch { get; set; }
        public Func<string, string, HttpResponseMessage>? OnDelete { get; set; }
        public int PostCount { get; private set; }
        public int DeleteCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (path.StartsWith("client/v4/", StringComparison.Ordinal))
                path = path["client/v4/".Length..];
            var query = request.RequestUri.Query;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return request.Method.Method switch
            {
                "GET" => OnGet?.Invoke(path, query) ?? Fail(),
                "POST" => IncrementPost(OnPost?.Invoke(path, body) ?? Fail()),
                "PUT" => OnPut?.Invoke(path, body) ?? Fail(),
                "PATCH" => OnPatch?.Invoke(path, body) ?? Fail(),
                "DELETE" => IncrementDelete(OnDelete?.Invoke(path, body) ?? Fail()),
                _ => Fail(),
            };
        }

        private HttpResponseMessage IncrementPost(HttpResponseMessage response)
        {
            PostCount++;
            return response;
        }

        private HttpResponseMessage IncrementDelete(HttpResponseMessage response)
        {
            DeleteCount++;
            return response;
        }
    }
}
