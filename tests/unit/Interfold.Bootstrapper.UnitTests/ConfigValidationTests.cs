using System.Diagnostics;
using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>Each test stages one invariant violation and asserts the validator throws
/// with a message naming the offending field — operators see it inline on failure.</summary>
public sealed class ConfigValidationTests
{
    private static BootstrapConfig MakeValid() => new()
    {
        Deployment =
        {
            OutputDir = "./deploy",
        },
        Edge =
        {
            Hosts = ["api.example.com"],
            Certificates =
            {
                RootCaName = "Interfold Root CA",
                CertYears = 5,
                TrustStoreInstall = true,
            },
            Ports =
            {
                Http = 80,
                Https = 443,
            },
        },
        Datastores =
        {
            Cql = { Backend = CqlBackend.ScyllaSingle },
        },
    };

    /// <summary>Mutates a valid config, invokes Validate, asserts every fragment appears in
    /// the thrown message. Bespoke-config / non-throwing tests stay inline.</summary>
    private static async Task AssertInvalidAsync(
        Action<BootstrapConfig> mutate,
        params string[] messageContains)
    {
        var cfg = MakeValid();
        mutate(cfg);
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        foreach (var frag in messageContains)
        {
            await Assert.That(ex.Message).Contains(frag);
        }
    }

    [Test]
    public async Task ValidConfigPasses()
    {
        ConfigPhase.Validate(MakeValid());
        await Task.CompletedTask;
    }

    [Test]
    public Task EmptyHostsListFailsValidation()
        => AssertInvalidAsync(c => c.Edge.Hosts = [], "hosts");

    [Test]
    public Task ZeroCertYearsFailsValidation()
        => AssertInvalidAsync(c => c.Edge.Certificates.CertYears = 0, "certYears");

    [Test]
    public Task NegativeCertYearsFailsValidation()
        => AssertInvalidAsync(c => c.Edge.Certificates.CertYears = -1, "certYears");

    [Test]
    public Task CertYearsOverThirtyFailsValidation()
        => AssertInvalidAsync(c => c.Edge.Certificates.CertYears = 31, "certYears");

    [Test]
    public Task EdgeHttpEqualsEdgeHttpsFailsValidation()
        => AssertInvalidAsync(c =>
        {
            c.Edge.Ports.Http = 443;
            c.Edge.Ports.Https = 443;
        }, "collides");

    [Test]
    public Task DomainWithSpaceFailsValidation()
        => AssertInvalidAsync(c => c.Edge.Hosts = ["api example.com"], "whitespace");

    [Test]
    public async Task Ipv4HostPassesValidation()
    {
        // LAN-only self-host: IP-SAN'd leaf; derived URL uses edge HTTPS.
        var cfg = MakeValid();
        cfg.Edge.Hosts = ["192.168.1.42"];

        ConfigPhase.Validate(cfg);
        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://192.168.1.42");
    }

    [Test]
    public async Task Ipv6HostPassesValidation()
    {
        // Derived URL must bracket-wrap the address per RFC 3986 §3.2.2.
        var cfg = MakeValid();
        cfg.Edge.Hosts = ["fe80::1"];

        ConfigPhase.Validate(cfg);
        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://[fe80::1]");
    }

    [Test]
    public async Task Ipv4CidrPlusDnsHostPassesValidation()
    {
        // DNS/IP acts as primary; CIDR widens root-CA Name Constraints scope.
        var cfg = MakeValid();
        cfg.Edge.Hosts = ["api.example.com", "10.0.0.0/8"];

        ConfigPhase.Validate(cfg);
        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://api.example.com");
    }

    [Test]
    public async Task Ipv6CidrPlusIpv4HostPassesValidation()
    {
        var cfg = MakeValid();
        cfg.Edge.Hosts = ["192.168.1.42", "fe80::/64"];

        ConfigPhase.Validate(cfg);
        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://192.168.1.42");
    }

    [Test]
    // CIDR-only has no leaf-eligible primary.
    public Task AllCidrHostsFailValidation()
        => AssertInvalidAsync(c => c.Edge.Hosts = ["192.168.1.0/24", "fe80::/64"], "non-CIDR");

    [Test]
    // Surface HostParser's fix-it rather than silently normalising.
    public Task CidrWithHostBitsSetFailsValidation()
        => AssertInvalidAsync(c => c.Edge.Hosts = ["192.168.1.42/24"],
            "host bits", "192.168.1.0/24", "192.168.1.42/32");

    [Test]
    public async Task DefaultConstructedDeploymentHasNoHosts()
    {
        // Pins the "no placeholder" contract on Edge.Hosts.
        var edge = new EdgeSection();
        await Assert.That(edge.Hosts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DefaultConstructedBootstrapConfigFailsValidation()
    {
        // Non-interactive callers omitting deployment.hosts must fail precisely, not silently.
        var cfg = new BootstrapConfig();

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(cfg));
        await Assert.That(ex.Message).Contains("hosts");
        await Assert.That(ex.Message).Contains("at least one");
    }

    [Test]
    public Task PortAboveMaxFailsValidation()
        => AssertInvalidAsync(c => c.Edge.Ports.Http = 70000, "Http");

    [Test]
    public async Task InvalidCqlBackendInJsonFailsDeserialization()
    {
        const string badJson = """
        {
            "datastores": { "cql": { "backend": "quadruple-redundant" } }
        }
        """;
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(badJson, BootstrapJsonContext.Default.BootstrapConfig));
        await Assert.That(ex.Message).Contains("CqlBackend");
        await Assert.That(ex.Message).Contains("backend");
    }

    [Test]
    public async Task DefaultPostgresDatabasePasses()
    {
        var cfg = MakeValid();
        cfg.Datastores.Postgres.Database = "interfold";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task CustomSafePostgresDatabasePasses()
    {
        // Exercise underscores + digits (typical env-suffixed name).
        var cfg = MakeValid();
        cfg.Datastores.Postgres.Database = "acme_prod_42";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public Task EmptyPostgresDatabaseFailsValidation()
        => AssertInvalidAsync(c => c.Datastores.Postgres.Database = string.Empty, "postgres.database");

    [Test]
    public Task WhitespacePostgresDatabaseFailsValidation()
        => AssertInvalidAsync(c => c.Datastores.Postgres.Database = "   ", "postgres.database");

    [Test]
    // Postgres tolerates a leading digit only inside quotes; forbid up front to avoid drift.
    public Task PostgresDatabaseStartingWithDigitFailsValidation()
        => AssertInvalidAsync(c => c.Datastores.Postgres.Database = "1interfold", "postgres.database");

    [Test]
    // Dashes need quoting; forbidding them keeps the name reusable as a role/schema prefix.
    public Task PostgresDatabaseWithDashFailsValidation()
        => AssertInvalidAsync(c => c.Datastores.Postgres.Database = "inter-fold", "postgres.database");

    [Test]
    public async Task DefaultClusterNamePasses()
    {
        var cfg = MakeValid();
        cfg.Datastores.Cql.ClusterName = "InterfoldCluster";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task CustomClusterNameWithSpacesPasses()
    {
        // Spaces are legitimate here (advertised in gossip / DESCRIBE CLUSTER).
        var cfg = MakeValid();
        cfg.Datastores.Cql.ClusterName = "Acme Prod 1.0";

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public Task EmptyClusterNameFailsValidation()
        => AssertInvalidAsync(c => c.Datastores.Cql.ClusterName = string.Empty, "clusterName");

    [Test]
    // A raw quote would corrupt the Cassandra entrypoint's cassandra.yaml rewrite.
    public Task ClusterNameWithSingleQuoteFailsValidation()
        => AssertInvalidAsync(c => c.Datastores.Cql.ClusterName = "Acme'Prod", "clusterName");

    [Test]
    public Task ClusterNameWithNewlineFailsValidation()
        => AssertInvalidAsync(c => c.Datastores.Cql.ClusterName = "Acme\nProd", "clusterName");

    [Test]
    // 64 chars is the published Cassandra limit.
    public Task OverlyLongClusterNameFailsValidation()
        => AssertInvalidAsync(c => c.Datastores.Cql.ClusterName = new string('A', 65), "clusterName");

    [Test]
    public async Task InvalidScyllaKeyspaceInJsonFailsDeserialization()
    {
        // Rejection lives in the JSON converter, not Validate.
        const string badJson = """
        {
            "datastores": { "cql": { "keyspace": "antarctica" } }
        }
        """;
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(badJson, BootstrapJsonContext.Default.BootstrapConfig));
        await Assert.That(ex.Message).Contains("ScyllaKeyspace");
        await Assert.That(ex.Message).Contains("keyspace");
    }

    [Test]
    public async Task EachValidScyllaKeyspacePasses()
    {
        // Iterates so future additions fail here first.
        foreach (var keyspace in Enum.GetValues<ScyllaKeyspace>())
        {
            var cfg = MakeValid();
            cfg.Datastores.Cql.Keyspace = keyspace;
            ConfigPhase.Validate(cfg);
        }
        await Task.CompletedTask;
    }

    [Test]
    public Task NonHttpCallbackBaseUrlFailsValidation()
        => AssertInvalidAsync(c => c.Api.OAuth.CallbackBaseUrl = "ftp://api.example.com", "callbackBaseUrl");

    [Test]
    // Even with a property-initialiser default, `"": ""` from hand-edited JSON must reject.
    public Task EmptyJwtAudienceFailsValidation()
        => AssertInvalidAsync(c => c.Api.OAuth.JwtAudience = "", "jwtAudience");

    [Test]
    public Task NonHttpCorsOriginFailsValidation()
        => AssertInvalidAsync(c => c.Api.CorsAllowedOrigins = ["https://app.example.com", "not-a-url"],
            "corsAllowedOrigins");

    [Test]
    public async Task ValidateFillsDerivedApiRuntimeDefaults()
    {
        // Validate is a mutating check — it materialises apiRuntime defaults for JSON callers.
        var cfg = MakeValid();
        cfg.Edge.Hosts = ["api.example.com", "admin.example.com"];
        cfg.Edge.Ports.Https = 443;
        cfg.Api.OAuth.CallbackBaseUrl = string.Empty;
        cfg.Api.OAuth.JwtAuthority = string.Empty;
        cfg.Api.CorsAllowedOrigins = [];

        ConfigPhase.Validate(cfg);

        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://api.example.com");
        await Assert.That(cfg.Api.OAuth.JwtAuthority).IsEqualTo("https://api.example.com");
        await Assert.That(cfg.Api.CorsAllowedOrigins.Count).IsEqualTo(2);
        await Assert.That(cfg.Api.CorsAllowedOrigins).Contains("https://api.example.com");
        await Assert.That(cfg.Api.CorsAllowedOrigins).Contains("https://admin.example.com");
    }

    [Test]
    public Task EdgeCloudflareTunnelWithoutDnsHostFails()
        => AssertInvalidAsync(c =>
        {
            c.Edge.Hosts = ["192.168.1.10"];
            c.Edge.Cloudflare.Enabled = true;
            c.Edge.Cloudflare.ApiToken = "cf-token";
            c.Edge.Cloudflare.TunnelName = "interfold";
        }, "Cloudflare Tunnel");

    [Test]
    public Task EdgeCloudflareTunnelRequiresApiToken()
        => AssertInvalidAsync(c =>
        {
            c.Edge.Cloudflare.Enabled = true;
            c.Edge.Cloudflare.ApiToken = "";
            c.Edge.Cloudflare.TunnelName = "interfold";
        }, "apiToken");

    [Test]
    public async Task EdgeCloudflareTunnelCoercesTlsModeToNone()
    {
        var cfg = MakeValid();
        cfg.Edge.TlsMode = EdgeTlsMode.PrivateCa;
        cfg.Edge.Cloudflare.Enabled = true;
        cfg.Edge.Cloudflare.ApiToken = "cf-token";
        cfg.Edge.Cloudflare.TunnelName = "interfold";
        cfg.Api.OAuth.CallbackBaseUrl = string.Empty;
        cfg.Api.OAuth.JwtAuthority = string.Empty;
        cfg.Api.CorsAllowedOrigins = [];

        ConfigPhase.Validate(cfg);

        await Assert.That(cfg.Edge.TlsMode).IsEqualTo(EdgeTlsMode.None);
        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://api.example.com");
    }

    [Test]
    public Task EdgeSubdomainRequiresHosts()
        => AssertInvalidAsync(c =>
        {
            c.Edge.Routing.Mode = EdgeRoutingMode.Subdomain;
            c.Edge.Routing.ApiHost = "";
            c.Edge.Routing.WebHost = "";
        }, "subdomain");

    [Test]
    public async Task V1JsonMigratesOnLoad()
    {
        const string json = """
            {
              "deployment": { "hosts": ["a.example.com"], "webHttps": true },
              "ports": { "apiHttp": 5000, "apiHttps": 5001, "webHttps": 8081 }
            }
            """;
        var result = ConfigSchemaMigrator.MigrateIfNeeded(json, "interfold.bootstrap.json", new PhaseLogger(
            TestSupport.MakeOptions(outputDir: Path.GetTempPath())));
        await Assert.That(result.DidMigrate).IsTrue();
        using var doc = JsonDocument.Parse(result.Json);
        await Assert.That(doc.RootElement.GetProperty("schemaVersion").GetInt32()).IsEqualTo(2);
        await Assert.That(doc.RootElement.GetProperty("edge").GetProperty("ports").GetProperty("https").GetInt32()).IsEqualTo(5001);
    }

    [Test]
    public async Task V2EdgeEnabledRejected()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "deployment": {
                "hosts": ["a.example.com"],
                "edge": { "tlsMode": "privateCa", "routing": "path", "enabled": true }
              },
              "ports": { "edgeHttp": 80, "edgeHttps": 443 }
            }
            """;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigSchemaMigrator.MigrateIfNeeded(json, "interfold.bootstrap.json", new PhaseLogger(
                TestSupport.MakeOptions(outputDir: Path.GetTempPath()))));
        await Assert.That(ex.Message).Contains("edge.enabled");
    }

    // --- Cluster / Storage / Observability / Socket / Persistence tuning validation ---

    [Test]
    public async Task InvalidNodeGroupInJsonFailsDeserialization()
    {
        // Rejection lives in the JSON converter, not Validate.
        const string badJson = """
        {
            "api": { "nodeGroup": "guardian" }
        }
        """;
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(badJson, BootstrapJsonContext.Default.BootstrapConfig));
        await Assert.That(ex.Message).Contains("NodeGroup");
        await Assert.That(ex.Message).Contains("nodeGroup");
    }

    [Test]
    public async Task EachValidNodeGroupPasses()
    {
        // Drives future allow-list extensions to fail here first.
        foreach (var nodeGroup in Enum.GetValues<NodeGroup>())
        {
            var cfg = MakeValid();
            cfg.Api.NodeGroup = nodeGroup;
            ConfigPhase.Validate(cfg);
        }
        await Task.CompletedTask;
    }

    [Test]
    public async Task EmptyOptionalStringsPass()
    {
        // Empty Avatar* / OtlpEndpoint = "feature disabled"; must not fail validation.
        var cfg = MakeValid();
        cfg.Api.Storage.AvatarStorageRoot = string.Empty;
        cfg.Api.Storage.AvatarPublicBase = string.Empty;
        cfg.Observability.OtlpEndpoint = string.Empty;
        cfg.Api.BatchBytesThreshold = null;

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public Task NonHttpAvatarPublicBaseFailsValidation()
        => AssertInvalidAsync(c => c.Api.Storage.AvatarPublicBase = "ftp://cdn.example.com/avatars/", "avatarPublicBase");

    [Test]
    // Relative paths resolve against the API container CWD → silent breakage.
    public Task RelativeAvatarStorageRootFailsValidation()
        => AssertInvalidAsync(c => c.Api.Storage.AvatarStorageRoot = "avatars", "avatarStorageRoot");

    [Test]
    public Task NonHttpOtlpEndpointFailsValidation()
        => AssertInvalidAsync(c => c.Observability.OtlpEndpoint = "grpc://otel-collector:4317", "otlpEndpoint");

    [Test]
    public Task ZeroDbRetryAttemptsFailsValidation()
        => AssertInvalidAsync(c => c.Api.Resilience.DbRetryAttempts = 0, "dbRetryAttempts");

    [Test]
    public Task DbRetryAttemptsAboveCapFailsValidation()
        => AssertInvalidAsync(c => c.Api.Resilience.DbRetryAttempts = 9999, "dbRetryAttempts");

    [Test]
    // Cross-check for the easy swap mistake (initial > max makes the cap below the start).
    public Task DbRetryMaxBelowInitialFailsValidation()
        => AssertInvalidAsync(c =>
        {
            c.Api.Resilience.DbRetryInitialDelayMs = 500;
            c.Api.Resilience.DbRetryMaxDelayMs = 100;
        }, "dbRetryMaxDelayMs", "dbRetryInitialDelayMs");

    [Test]
    public Task HydrationConcurrencyAboveCapFailsValidation()
        => AssertInvalidAsync(c => c.Api.Resilience.HydrationMaxConcurrency = 9999, "hydrationMaxConcurrency");

    [Test]
    // Nullable → null passes, but 1..16 MiB range enforced when supplied.
    public Task SocketBatchThresholdOutOfRangeFailsValidation()
        => AssertInvalidAsync(c => c.Api.BatchBytesThreshold = 0, "batchBytesThreshold");

    [Test]
    public async Task DefaultBackupSectionPasses()
    {
        // Shipped defaults are the "no-op" stance and must pass day one.
        var cfg = MakeValid();
        await Assert.That(cfg.Deployment.Backup.RetainCount).IsEqualTo(14);
        await Assert.That(cfg.Deployment.Backup.Schedule).IsEqualTo("daily");
        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    // 0 would delete every backup as it's written — likely a typo for `enabled=false`.
    public Task ZeroBackupRetainCountFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.Backup.RetainCount = 0, "retainCount");

    [Test]
    public Task NegativeBackupRetainCountFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.Backup.RetainCount = -5, "retainCount");

    [Test]
    // 1000 is the documented cap; larger values are almost always a units mistake.
    public Task BackupRetainCountAboveCapFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.Backup.RetainCount = 5000, "retainCount");

    [Test]
    public Task EmptyBackupScheduleFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.Backup.Schedule = string.Empty, "schedule");

    [Test]
    public Task WhitespaceBackupScheduleFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.Backup.Schedule = "   ", "schedule");

    [Test]
    // Belt-and-braces: string lands on OnCalendar=, but reject shell chaining upfront.
    public Task BackupScheduleWithShellMetacharactersFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.Backup.Schedule = "daily;rm -rf /", "schedule");

    [Test]
    public async Task WellKnownBackupScheduleShortcutsPass()
    {
        foreach (var schedule in new[] { "hourly", "daily", "weekly", "monthly", "*-*-* 03:00", "Mon..Fri 03:30", "Sat *-*-* 04,16:00:00" })
        {
            var cfg = MakeValid();
            cfg.Deployment.Backup.Schedule = schedule;
            ConfigPhase.Validate(cfg);
        }
        await Task.CompletedTask;
    }

    [Test]
    // Relative paths resolve against systemd's unpredictable CWD.
    public Task RelativeBackupDirectoryFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.Backup.Directory = "backups/interfold", "directory");

    [Test]
    public async Task EmptyBackupDirectoryPasses()
    {
        // Empty = default ({outputDir}/backups).
        var cfg = MakeValid();
        cfg.Deployment.Backup.Directory = string.Empty;

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task AutostartTogglePassesIndependently()
    {
        // Toggles are orthogonal.
        foreach (var enabled in new[] { true, false })
        {
            foreach (var autostart in new[] { true, false })
            {
                var cfg = MakeValid();
                cfg.Deployment.Backup.Enabled = enabled;
                cfg.Deployment.AutostartServer = autostart;
                ConfigPhase.Validate(cfg);
            }
        }
        await Task.CompletedTask;
    }

    [Test]
    public Task UpdateHealthCheckTimeoutZeroFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.Update.HealthCheckTimeoutSeconds = 0, "healthCheckTimeoutSeconds");

    [Test]
    public Task UpdateHealthCheckTimeoutAboveMaxFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.Update.HealthCheckTimeoutSeconds = 3601, "healthCheckTimeoutSeconds");

    [Test]
    public async Task UpdateHealthCheckTimeoutInRangePasses()
    {
        // 1s and 3600s are the inclusive bounds.
        var cfg = MakeValid();
        cfg.Deployment.Update.HealthCheckTimeoutSeconds = 1;
        ConfigPhase.Validate(cfg);

        cfg.Deployment.Update.HealthCheckTimeoutSeconds = 3600;
        ConfigPhase.Validate(cfg);

        await Task.CompletedTask;
    }

    [Test]
    // Fail here with a clear name instead of "no such service" from docker compose.
    public Task UpdateServicesUnknownEntryFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.Update.Services = ["msg-database"], "msg-database", "msg-db");

    [Test]
    public Task UpdateServicesBlankEntryFailsValidation()
        => AssertInvalidAsync(c => c.Deployment.Update.Services = ["msg-db", ""], "blank entry");

    [Test]
    public async Task UpdateServicesKnownEntriesPassValidation()
    {
        foreach (var svc in ConfigPhase.ValidUpdateServices)
        {
            var cfg = MakeValid();
            cfg.Deployment.Update.Services = [svc];
            ConfigPhase.Validate(cfg);
        }
        await Task.CompletedTask;
    }

    [Test]
    public async Task UpdateServicesEmptyPasses()
    {
        // Empty = "every service" (the default).
        var cfg = MakeValid();
        cfg.Deployment.Update.Services = [];

        ConfigPhase.Validate(cfg);
        await Task.CompletedTask;
    }

    [Test]
    public async Task UpdateTogglePassesIndependently()
    {
        // All three toggles are orthogonal.
        foreach (var enabled in new[] { true, false })
        {
            foreach (var autoRestore in new[] { true, false })
            {
                foreach (var recreate in new[] { true, false })
                {
                    var cfg = MakeValid();
                    cfg.Deployment.Update.Enabled = enabled;
                    cfg.Deployment.Update.AutoRestoreOnFailure = autoRestore;
                    cfg.Deployment.Update.RecreateOnUpdate = recreate;
                    ConfigPhase.Validate(cfg);
                }
            }
        }
        await Task.CompletedTask;
    }

    [Test]
    public async Task MalformedJsonReturnsClearError()
    {
        // Drive `publish` so prereqs are skipped (Windows/macOS-friendly).
        using var scratch = TestSupport.NewScratchDir("interfold-cfg-malformed");
        var tmpDir = scratch.Path;
        var configPath = Path.Combine(tmpDir, "interfold.bootstrap.json");
        await File.WriteAllTextAsync(configPath, "{ this is not valid json");

        var result = await RunBootstrapperAsync("publish", "--config", configPath,
            "--output-dir", tmpDir, "--non-interactive");

        await Assert.That(result.ExitCode).IsNotEqualTo(0)
            .Because("malformed JSON must abort the bootstrap");
        // JsonException message varies by SDK version but always mentions parsing.
        var combined = result.Stdout + result.Stderr;
        await Assert.That(combined).Contains("JSON")
            .Or.Contains("json")
            .Or.Contains("parse");
    }

    [Test]
    public async Task MissingFileWithNonInteractiveExitsWithMessage()
    {
        // Drive `publish` so prereqs are skipped on non-Linux hosts.
        using var scratch = TestSupport.NewScratchDir("interfold-cfg-missing-ni");
        var tmpDir = scratch.Path;
        var configPath = Path.Combine(tmpDir, "interfold.bootstrap.json");
        await Assert.That(File.Exists(configPath)).IsFalse();

        var result = await RunBootstrapperAsync("publish", "--config", configPath,
            "--output-dir", tmpDir, "--non-interactive");

        await Assert.That(result.ExitCode).IsNotEqualTo(0);
        await Assert.That(result.Stdout + result.Stderr)
            .Contains("Config file not found")
            .Or.Contains("non-interactive");
    }

    /// <summary>Shells out to the compiled binary so tests exercise the operator dispatch
    /// path; TestSupport.BootstrapperBinaryOrSkip locates the Debug build.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunBootstrapperAsync(params string[] args)
    {
        var path = TestSupport.BootstrapperBinaryOrSkip();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Redirecting stdin eliminates the prompt path even if the host has a TTY.
            RedirectStandardInput = true,
        };
        psi.ArgumentList.Add(path);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet");
        proc.StandardInput.Close();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }
}
