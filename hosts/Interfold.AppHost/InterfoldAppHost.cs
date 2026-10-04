using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Interfold.AppHost.DevSeed;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
// Aspire.Hosting.ApplicationModel also defines PersistenceMode; alias to disambiguate.
using PersistenceMode = Interfold.Shared.Contracts.PersistenceMode;

namespace Interfold.AppHost;

/// <summary>Interfold distributed-application resource graph. Consumed by the dev
/// <c>Interfold.AppHost</c> executable (`aspire run`) and by the self-hosting
/// <c>Interfold.Bootstrapper</c> which pre-populates <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
/// with generated secrets before calling <see cref="Configure"/>.</summary>
public static class InterfoldAppHost
{
    private const string HttpEndpointName = "http";
    private const string HttpsEndpointName = "https";

    private const string ComposeEnvironmentName = "docker-compose";

    /// <summary>Aspire's default dashboard service name (<c>{environment}-dashboard</c>).</summary>
    private const string DashboardComposeServiceName = ComposeEnvironmentName + "-dashboard";

    private const int DefaultApiContainerHttpPort = 5100;
    private const int DefaultEdgeHttpPort = 80;
    private const int DefaultEdgeHttpsPort = 443;

    // Browsers resolve *.localhost to loopback, so aspire run can exercise subdomain
    // routing without bootstrapper DNS. Parameters:edge-api-host / edge-web-host override.
    private const string DefaultDevSubdomainApiHost = "api.localhost";
    private const string DefaultDevSubdomainWebHost = "web.localhost";

    /// <summary>Registers the full Interfold resource graph. Does not call
    /// <c>Build()</c> or <c>Run()</c>.</summary>
    public static void Configure(IDistributedApplicationBuilder builder)
    {
        int Port(string key, int fallback) => int.TryParse(builder.Configuration[key], out var p) ? p : fallback;

        // ParamName keeps parameter declaration and IConfiguration override on the same spelling.
        static string ParamName(string key) => AppHostParameterKeys.ToParameterName(key);
        // Must match the API image's internal Kestrel listen port (5100). Override via
        // --Ports:api-container-http= when rebuilding the image with a different EXPOSE.
        var apiContainerHttpPort = Port(AppHostParameterKeys.PortsApiContainerHttp, DefaultApiContainerHttpPort);
        var edgeHttpPort = Port(AppHostParameterKeys.PortsEdgeHttp, DefaultEdgeHttpPort);
        var edgeHttpsPort = Port(AppHostParameterKeys.PortsEdgeHttps, DefaultEdgeHttpsPort);

        var includeEdge = true;
        var edgeTlsMode = builder.Configuration[AppHostParameterKeys.EdgeTlsMode] ?? "privateCa";
        var edgeUsesPlainHttp = string.Equals(edgeTlsMode, "none", StringComparison.OrdinalIgnoreCase);
        var edgeCloudflareTunnel = BoolWire.ParseToggle(
            builder.Configuration[AppHostParameterKeys.EdgeCloudflareTunnel], fallback: false);
        var edgeServerName = builder.Configuration[AppHostParameterKeys.EdgeServerName];
        if (string.IsNullOrWhiteSpace(edgeServerName)) edgeServerName = "_";
        // Same key PublishPhase injects. Off in publish so those hosts are left alone.
        var runModeSubdomain = !builder.ExecutionContext.IsPublishMode
            && string.Equals(
                builder.Configuration[AppHostParameterKeys.EdgeRouting],
                "subdomain",
                StringComparison.OrdinalIgnoreCase);
        static string HostOr(string? configured, string fallback) =>
            string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
        var devApiHost = HostOr(
            builder.Configuration[AppHostParameterKeys.EdgeApiHost],
            runModeSubdomain ? DefaultDevSubdomainApiHost : "localhost");
        var devWebHost = HostOr(
            builder.Configuration[AppHostParameterKeys.EdgeWebHost],
            runModeSubdomain ? DefaultDevSubdomainWebHost : "localhost");
        // Run mode without leaf certs uses the plaintext nginx template (see includeEdge).
        // JWT / CORS / OAuth callback must match that browser origin, not tlsMode=privateCa.
        var runModeMissingLeafCerts = !builder.ExecutionContext.IsPublishMode
            && !edgeUsesPlainHttp
            && !File.Exists(Path.Combine(AppHostRepoPaths.ResolveRepoRoot(), "certs", "leaf.crt"));
        var publicEdgeIsHttp = edgeUsesPlainHttp || runModeMissingLeafCerts;
        string PublicOrigin(string host)
        {
            if (publicEdgeIsHttp)
            {
                var suffix = edgeHttpPort == DefaultEdgeHttpPort ? string.Empty : $":{edgeHttpPort}";
                return $"http://{host}{suffix}";
            }

            var httpsSuffix = edgeHttpsPort == DefaultEdgeHttpsPort ? string.Empty : $":{edgeHttpsPort}";
            return $"https://{host}{httpsSuffix}";
        }

        // Wasm/mobile paths already include /api/... and /auth/.... Path mode is one host.
        // Subdomain splits them: JWT and OAuth callbacks use the API host; CORS and OTLP
        // follow the web host the page is actually on.
        var publicApiBase = PublicOrigin(devApiHost);
        var publicWebOrigin = PublicOrigin(devWebHost);

        // Publish stamps the web image from the hosts nginx already received. Path mode
        // has no api host, so the server name is the origin. Tunnel TLS is public https
        // even though the origin itself is plaintext.
        string DeploymentApiOrigin()
        {
            if (!builder.ExecutionContext.IsPublishMode)
                return publicApiBase;

            var host = HostOr(builder.Configuration[AppHostParameterKeys.EdgeApiHost], edgeServerName);
            if (host == "_")
                return string.Empty;
            // server_name keeps a bare IPv6 literal; the client URL needs brackets.
            if (host.Contains(':'))
                host = $"[{host.Trim().TrimStart('[').TrimEnd(']')}]";
            if (edgeCloudflareTunnel)
                return $"https://{host}";
            return PublicOrigin(host);
        }

        if (!builder.ExecutionContext.IsPublishMode)
        {
            // Browser OTLP into this dashboard. Wasm discovery only returns a URL
            // (no x-otlp-api-key), so OTLP ingest is unsecured for aspire run.
            // Match the host, not a subdomain of localhost (web.localhost must not become web.127.0.0.1).
            var otlpCorsOrigins = publicWebOrigin.Contains("://localhost", StringComparison.OrdinalIgnoreCase)
                ? $"{publicWebOrigin},{publicWebOrigin.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase)}"
                : publicWebOrigin;
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aspire:Dashboard:Otlp:Cors:AllowedOrigins"] = otlpCorsOrigins,
                ["Aspire:Dashboard:Otlp:Cors:AllowedHeaders"] = "*",
                ["Aspire:Dashboard:Otlp:AuthMode"] = "Unsecured",
            });
        }

        var includeApi = BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.IncludeApi], fallback: true);
        var persistentContainers = builder.ExecutionContext.IsPublishMode
            || BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.PersistentContainers], fallback: false);

        // Blank → Sqlite.
        var persistenceMode = EnumWireExtensions.ParsePersistenceMode(
            builder.Configuration[AppHostParameterKeys.Persistence]);
        var useSqlite = persistenceMode == PersistenceMode.Sqlite;

        // Host-side SQLite file used by DevSeed + AddProject; container path bind-mounts the
        // same directory so secrets preload sees the seeded rows.
        var sqliteHostDir = Path.Combine(builder.AppHostDirectory, ".data", "sqlite");
        var sqliteHostDbPath = Path.Combine(sqliteHostDir, ContainerMountPaths.InterfoldSqliteDbFileName);
        var sqliteHostConnectionString = $"Data Source={sqliteHostDbPath}";
        var sqliteContainerConnectionString =
            $"Data Source={ContainerMountPaths.InterfoldSqliteData}/{ContainerMountPaths.InterfoldSqliteDbFileName}";

        // Bootstrapper sets include-dashboard=false so self-hosted production stacks
        // don't pull the nightly aspire-dashboard image; dev `aspire run` keeps it.
        var includeDashboard = BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.IncludeDashboard], fallback: true);
        builder.AddDockerComposeEnvironment(ComposeEnvironmentName)
            .WithDashboard(includeDashboard)
            .ConfigureComposeFile(compose =>
            {
                compose.AddNetwork(new Network { Name = ComposeNetworks.EdgeApi, Driver = "bridge" });
                compose.AddNetwork(new Network { Name = ComposeNetworks.EdgeWeb, Driver = "bridge" });

                if (compose.Services.TryGetValue(DashboardComposeServiceName, out var dashboard))
                {
                    dashboard.Networks.Add(ComposeNetworks.EdgeApi);
                }
            });

        // The <user>_admin superuser is minted by DatabaseInitPhase into internal.secrets and
        // is never surfaced here.
        // GenerateParameterDefault covers the dev `aspire run` case (bootstrapper always injects
        // a real PEM in publish mode, config wins over the default). The generated value is a
        // random alphanumeric string — RecoveryCodeResolver.TryLoadEncryptionPrivateKey checks
        // for "BEGIN" and short-circuits, so JWE-encrypted recovery codes silently 400 in dev.
        // That's the accepted trade-off for the no-config F5 target.
        var encryptionPrivateKey = builder.AddParameter(ParamName(AppHostParameterKeys.EncryptionPrivateKey),
            new GenerateParameterDefault { MinLength = 32 }, secret: true, persist: true);

        // Public OAuth client IDs — empty default disables the corresponding provider (see
        // OAuthChallengeServiceCollectionExtensions). Secrets are run-mode only: publish
        // keeps them in internal.secrets via DatabaseInitPhase, never compose .env.
        var googleOAuthClientId = builder.AddConfiguredParameter(AppHostParameterKeys.GoogleOAuthClientId, publishValueAsDefault: true);
        var discordOAuthClientId = builder.AddConfiguredParameter(AppHostParameterKeys.DiscordOAuthClientId, publishValueAsDefault: true);
        var appleOAuthClientId = builder.AddConfiguredParameter(AppHostParameterKeys.AppleOAuthClientId, publishValueAsDefault: true);

        IResourceBuilder<ParameterResource>? googleOAuthClientSecret = null;
        IResourceBuilder<ParameterResource>? discordOAuthClientSecret = null;
        IResourceBuilder<ParameterResource>? appleOAuthClientSecret = null;
        if (!builder.ExecutionContext.IsPublishMode)
        {
            googleOAuthClientSecret = builder.AddConfiguredParameter(AppHostParameterKeys.GoogleOAuthClientSecret, secret: true);
            discordOAuthClientSecret = builder.AddConfiguredParameter(AppHostParameterKeys.DiscordOAuthClientSecret, secret: true);
            appleOAuthClientSecret = builder.AddConfiguredParameter(AppHostParameterKeys.AppleOAuthClientSecret, secret: true);
        }

        // API runtime parameters managed end-to-end by the bootstrapper (ConfigPhase prompts,
        // ConfigureApiSelfHostEnv pipes to the container as OCTOCON_*).
        var oauthCallbackBaseUrl = builder.AddConfiguredParameter(AppHostParameterKeys.OAuthCallbackBaseUrl, publishValueAsDefault: true);
        var jwtAuthority = builder.AddConfiguredParameter(AppHostParameterKeys.JwtAuthority, publishValueAsDefault: true);
        var jwtAudience = builder.AddConfiguredParameter(AppHostParameterKeys.JwtAudience, "octocon", publishValueAsDefault: true);
        var corsAllowedOrigins = builder.AddConfiguredParameter(AppHostParameterKeys.CorsAllowedOrigins, publishValueAsDefault: true);

        // Operator tuning — every default matches the API's compile-time fallback so a
        // fresh bootstrap reproduces "env var unset" behaviour. Empty for the nullable
        // knobs (avatars, OTLP, socket threshold) — ApplyStorage / ApplyObservability
        // normalise empty → null.
        var nodeGroup = builder.AddConfiguredParameter(AppHostParameterKeys.NodeGroup, "auxiliary", publishValueAsDefault: true);
        var avatarPublicBase = builder.AddConfiguredParameter(AppHostParameterKeys.AvatarPublicBase, publishValueAsDefault: true);
        var otlpEndpoint = builder.AddConfiguredParameter(AppHostParameterKeys.OtlpEndpoint, publishValueAsDefault: true);
        var advertiseOtlpToClients = builder.AddConfiguredParameter(AppHostParameterKeys.AdvertiseOtlpToClients, "false", publishValueAsDefault: true);
        var clientOtlpHttpEndpoint = builder.AddConfiguredParameter(AppHostParameterKeys.ClientOtlpHttpEndpoint, publishValueAsDefault: true);
        var socketBatchBytesThreshold = builder.AddConfiguredParameter(AppHostParameterKeys.SocketBatchBytesThreshold, publishValueAsDefault: true);
        var dbRetryAttempts = builder.AddConfiguredParameter(AppHostParameterKeys.DbRetryAttempts, "3", publishValueAsDefault: true);
        var dbRetryInitialDelayMs = builder.AddConfiguredParameter(AppHostParameterKeys.DbRetryInitialDelayMs, "100", publishValueAsDefault: true);
        var dbRetryMaxDelayMs = builder.AddConfiguredParameter(AppHostParameterKeys.DbRetryMaxDelayMs, "1500", publishValueAsDefault: true);
        var hydrationMaxConcurrency = builder.AddConfiguredParameter(AppHostParameterKeys.HydrationMaxConcurrency, "8", publishValueAsDefault: true);
        var cfAccessTeamDomain = builder.AddConfiguredParameter(AppHostParameterKeys.CfAccessTeamDomain, publishValueAsDefault: true);
        var cfAccessAud = builder.AddConfiguredParameter(AppHostParameterKeys.CfAccessAud, publishValueAsDefault: true);
        var cfAccessDiscordIdpId = builder.AddConfiguredParameter(AppHostParameterKeys.CfAccessDiscordIdpId, publishValueAsDefault: true);

        // Seed ownership by mode:
        //  - Publish: DatabaseInitPhase in the bootstrapper.
        //  - RunMode + sqlite: SqliteDevSeedHostedService (migrate + secrets only).
        IResourceBuilder<SqliteDevSeedResource>? devSeedResource = null;
        if (!builder.ExecutionContext.IsPublishMode && includeApi && useSqlite)
        {
            Directory.CreateDirectory(sqliteHostDir);
            devSeedResource = builder.AddSqliteDevSeedPipeline(sqliteHostDbPath);
        }

        // Interfold API: pre-built image for self-hosting (Parameters:api-image), csproj build
        // for dev (`aspire run`).
        IResourceBuilder<IResourceWithEndpoints>? apiResource = null;
        var apiIsContainer = false;
        if (includeApi)
        {
            void ConfigureApiCommon(IResourceBuilder<IResourceWithEnvironment> api, bool containerPath)
            {
                api.WithEnvironment(ContainerEnvNames.EncryptionPrivateKey, encryptionPrivateKey)
                   .WithEnvironment(OctoconEnvKeys.GoogleOAuthClientId, googleOAuthClientId)
                   .WithEnvironment(OctoconEnvKeys.DiscordOAuthClientId, discordOAuthClientId)
                   .WithEnvironment(OctoconEnvKeys.AppleOAuthClientId, appleOAuthClientId);
                // Run-mode only — publish seeds these via DatabaseInitPhase into internal.secrets
                // and must not leak them into compose .env.
                if (googleOAuthClientSecret is not null)
                {
                    api.WithEnvironment(OctoconEnvKeys.GoogleOAuthClientSecret, googleOAuthClientSecret)
                       .WithEnvironment(OctoconEnvKeys.DiscordOAuthClientSecret, discordOAuthClientSecret!)
                       .WithEnvironment(OctoconEnvKeys.AppleOAuthClientSecret, appleOAuthClientSecret!);
                }

                if (persistenceMode == PersistenceMode.InMemory)
                {
                    api.WithEnvironment(OctoconEnvKeys.Persistence, PersistenceMode.InMemory.ToWire());
                    return;
                }

                var sqliteCs = containerPath ? sqliteContainerConnectionString : sqliteHostConnectionString;
                api.WithEnvironment(OctoconEnvKeys.Persistence, PersistenceMode.Sqlite.ToWire())
                   .WithEnvironment(OctoconEnvKeys.SqliteConnection, sqliteCs);
            }

            void AttachSqliteDataMount(IResourceBuilder<ContainerResource> api)
            {
                // Bootstrapper injects an absolute host path so migrate/seed/backup share the
                // same file the API container sees. Blank → named volume (publish) or AppHost
                // .data/sqlite bind (aspire run).
                var injectedHostPath = builder.Configuration[AppHostParameterKeys.SqliteDataHostPath];
                if (!string.IsNullOrWhiteSpace(injectedHostPath))
                {
                    Directory.CreateDirectory(injectedHostPath);
                    api.WithBindMount(injectedHostPath, ContainerMountPaths.InterfoldSqliteData);
                }
                else if (builder.ExecutionContext.IsPublishMode && persistentContainers)
                {
                    api.WithVolume(ComposeVolumes.InterfoldSqliteData, ContainerMountPaths.InterfoldSqliteData);
                }
                else
                {
                    api.WithBindMount(sqliteHostDir, ContainerMountPaths.InterfoldSqliteData);
                }
            }

            void WaitForApiDependencies<T>(IResourceBuilder<T> api) where T : IResourceWithWaitSupport
            {
                if (devSeedResource is not null)
                    api.WaitFor(devSeedResource);
            }

            // Dev-project path only. ConfigureApiSelfHostEnv covers the container path via the
            // operator-provided Parameters:jwt-authority etc. Run mode has no operator, so these
            // follow the edge origin the browser actually uses (one localhost host in path mode;
            // API host for JWT/callbacks and web host for CORS when edge-routing=subdomain).
            void ConfigureApiDevEnv(IResourceBuilder<IResourceWithEnvironment> api)
            {
                api.WithEnvironment(OctoconEnvKeys.JwtAuthority, publicApiBase)
                   .WithEnvironment(OctoconEnvKeys.JwtAudience, "octocon")
                   .WithEnvironment(OctoconEnvKeys.AuthCallbackBaseUrl, publicApiBase)
                   .WithEnvironment(OctoconEnvKeys.CorsAllowedOrigins, publicWebOrigin);

                var clientOtlpOverride = builder.Configuration[AppHostParameterKeys.ClientOtlpHttpEndpoint];
                var dashboardOtlpHttp = builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"];
                var clientOtlp = !string.IsNullOrWhiteSpace(clientOtlpOverride)
                    ? clientOtlpOverride
                    : dashboardOtlpHttp ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(clientOtlp))
                {
                    api.WithEnvironment(OctoconEnvKeys.ClientOtlpHttpEndpoint, clientOtlp)
                       .WithEnvironment(OctoconEnvKeys.AdvertiseOtlpToClients, "true");
                }
            }

            // Self-hosting only. Avatars always bind-mounted; /certs when private CA material
            // exists for TrustController.
            void ConfigureApiSelfHostEnv(IResourceBuilder<ContainerResource> api)
            {
                api.WithBindMount(AvatarsPaths.HostDir, AvatarsPaths.ContainerDir, isReadOnly: false)
                   .WithEnvironment(ContainerEnvNames.AspNetCoreHttpPorts, apiContainerHttpPort.ToString())
                   .WithEnvironment(OctoconEnvKeys.AuthCallbackBaseUrl, oauthCallbackBaseUrl)
                   .WithEnvironment(OctoconEnvKeys.JwtAuthority, jwtAuthority)
                   .WithEnvironment(OctoconEnvKeys.JwtAudience, jwtAudience)
                   .WithEnvironment(OctoconEnvKeys.CorsAllowedOrigins, corsAllowedOrigins)
                   .WithEnvironment(OctoconEnvKeys.NodeGroup, nodeGroup)
                   .WithEnvironment(OctoconEnvKeys.AvatarStorageRoot, ContainerMountPaths.InterfoldAvatars)
                   .WithEnvironment(OctoconEnvKeys.AvatarPublicBase, avatarPublicBase)
                   .WithEnvironment(OctoconEnvKeys.OtlpEndpoint, otlpEndpoint)
                   .WithEnvironment(OctoconEnvKeys.AdvertiseOtlpToClients, advertiseOtlpToClients)
                   .WithEnvironment(OctoconEnvKeys.ClientOtlpHttpEndpoint, clientOtlpHttpEndpoint)
                   .WithEnvironment(OctoconEnvKeys.SocketBatchBytesThreshold, socketBatchBytesThreshold)
                   .WithEnvironment(OctoconEnvKeys.DbRetryAttempts, dbRetryAttempts)
                   .WithEnvironment(OctoconEnvKeys.DbRetryInitialDelayMs, dbRetryInitialDelayMs)
                   .WithEnvironment(OctoconEnvKeys.DbRetryMaxDelayMs, dbRetryMaxDelayMs)
                   .WithEnvironment(OctoconEnvKeys.HydrationMaxConcurrency, hydrationMaxConcurrency)
                   .WithEnvironment(OctoconEnvKeys.CfAccessTeamDomain, cfAccessTeamDomain)
                   .WithEnvironment(OctoconEnvKeys.CfAccessAud, cfAccessAud)
                   .WithEnvironment(OctoconEnvKeys.CfAccessDiscordIdpId, cfAccessDiscordIdpId);

                // Mount root CA for TrustController whenever edge uses private CA material.
                if (!edgeUsesPlainHttp && !edgeCloudflareTunnel)
                {
                    api.WithBindMount(CertsPaths.HostDir, CertsPaths.ContainerDir, isReadOnly: true)
                       .WithEnvironment(OctoconEnvKeys.TrustRootCaPath, CertsPaths.RootCaCrt)
                       .WithEnvironment(OctoconEnvKeys.TrustRootCaFingerprintPath, CertsPaths.RootCaFingerprint);
                }
            }

            var apiImageRef = builder.Configuration[AppHostParameterKeys.ApiImage];
            if (!string.IsNullOrEmpty(apiImageRef))
            {
                var apiImage = ImageRef.Parse(apiImageRef);
                var apiContainer = builder.AddContainer(ComposeServices.InterfoldApi, apiImage.Image, apiImage.Tag)
                    .PullAlwaysInRunMode(apiImage)
                    .WithContainerNetworkAlias(ComposeServices.InterfoldApi)
                    .WithHttpEndpoint(targetPort: apiContainerHttpPort, name: HttpEndpointName)
                    .WithHttpHealthCheck(HealthEndpoints.Ready, endpointName: HttpEndpointName)
                    .PublishAsDockerComposeService(ApiComposeServicePublisher(apiContainerHttpPort));

                apiResource = apiContainer;
                apiIsContainer = true;
                apiContainer.WithUrl(publicApiBase, "edge");
                ConfigureApiCommon(apiContainer, containerPath: true);
                ConfigureApiSelfHostEnv(apiContainer);
                if (useSqlite)
                    AttachSqliteDataMount(apiContainer);
                WaitForApiDependencies(apiContainer);
            }
            else
            {
                var apiProject = builder.AddProject<Projects.Interfold_Api_Host>(ComposeServices.InterfoldApi)
                    .WithHttpEndpoint(targetPort: apiContainerHttpPort, name: HttpEndpointName)
                    .WithHttpHealthCheck(HealthEndpoints.Ready, endpointName: HttpEndpointName)
                    .PublishAsDockerComposeService(ApiComposeServicePublisher(apiContainerHttpPort));
                apiResource = apiProject;
                apiProject.WithUrl(publicApiBase, "edge");
                ConfigureApiCommon(apiProject, containerPath: false);
                ConfigureApiDevEnv(apiProject);
                WaitForApiDependencies(apiProject);
            }
        }

        var includeWeb = BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.IncludeWeb], fallback: true);
        IResourceBuilder<ContainerResource>? web = null;
        if (includeWeb)
        {
            var webImageRef = builder.Configuration[AppHostParameterKeys.WebImage];
            if (string.IsNullOrWhiteSpace(webImageRef))
                webImageRef = DefaultContainerImages.Web;
            var webImage = ImageRef.Parse(webImageRef);
            web = builder.AddContainer(ComposeServices.InterfoldWeb, webImage.Image, webImage.Tag)
                .PullAlwaysInRunMode(webImage)
                .WithContainerNetworkAlias(ComposeServices.InterfoldWeb)
                .WithHttpEndpoint(targetPort: 8080, name: HttpEndpointName)
                .WithHttpHealthCheck("/", endpointName: HttpEndpointName);
            web.WithEnvironment(ContainerEnvNames.InterfoldDefaultApiEndpoint, DeploymentApiOrigin());
            if (!builder.ExecutionContext.IsPublishMode)
                web.WithUrl(publicWebOrigin, "edge");

            web.PublishAsDockerComposeService((_, service) =>
            {
                service.Networks = [ComposeNetworks.EdgeWeb];
                service.Healthcheck = ComposeHealthcheck.Cmd(
                    interval: "15s", timeout: "5s", retries: 5, startPeriod: "10s",
                    command: ["curl", "-f", "http://localhost:8080/"]);
            });
        }

        if (includeEdge)
        {
            // Publish keeps bootstrapper-injected hosts. Path mode stays on nginx's `_`
            // catch-all so 127.0.0.1 still matches. Subdomain run mode uses the resolved hosts.
            var edgeApiHost = runModeSubdomain
                ? devApiHost
                : builder.Configuration[AppHostParameterKeys.EdgeApiHost] ?? edgeServerName;
            var edgeWebHost = runModeSubdomain
                ? devWebHost
                : builder.Configuration[AppHostParameterKeys.EdgeWebHost] ?? edgeServerName;
            var includeWebUpstream = BoolWire.ParseToggle(
                builder.Configuration[AppHostParameterKeys.EdgeIncludeWebUpstream], fallback: includeWeb);

            // Tunnel: private HTTP origin only (no host-published ports). Otherwise publish
            // edge HTTP, and HTTPS + leaf certs when tlsMode is privateCa.
            var edge = edgeCloudflareTunnel
                ? builder.AddContainer(ComposeServices.EdgeNginx, "nginx", "1.27-alpine")
                .PullAlwaysInRunMode()
                .WithHttpEndpoint(targetPort: 80, name: HttpEndpointName)
                : builder.AddContainer(ComposeServices.EdgeNginx, "nginx", "1.27-alpine")
                .PullAlwaysInRunMode()
                .WithHttpEndpoint(port: edgeHttpPort, targetPort: 80, name: HttpEndpointName);

            var nginxTemplateSource = $"{EdgePaths.HostSupportDir}/default.conf.template";
            var nginxProxyParamsSource = $"{EdgePaths.HostSupportDir}/proxy_params.conf";
            if (!builder.ExecutionContext.IsPublishMode)
            {
                var repoRoot = AppHostRepoPaths.ResolveRepoRoot();
                nginxTemplateSource = EdgePaths.SourceTemplate(
                    repoRoot, runModeSubdomain, publicEdgeIsHttp);
                nginxProxyParamsSource = EdgePaths.SourceProxyParams(repoRoot);
            }

            edge = edge
                .WithBindMount(nginxTemplateSource, EdgePaths.ContainerNginxTemplate, isReadOnly: true)
                .WithBindMount(nginxProxyParamsSource, EdgePaths.ContainerProxyParams, isReadOnly: true);

            if (!edgeCloudflareTunnel && !edgeUsesPlainHttp && !runModeMissingLeafCerts)
            {
                edge = edge
                    .WithHttpsEndpoint(port: edgeHttpsPort, targetPort: 443, name: HttpsEndpointName)
                    .WithBindMount(EdgePaths.HostCertsDir, EdgePaths.ContainerCertsDir, isReadOnly: true)
                    .WithEnvironment(ContainerEnvNames.NginxSslCertFile, EdgePaths.LeafCrt)
                    .WithEnvironment(ContainerEnvNames.NginxSslKeyFile, EdgePaths.LeafKey)
                    .WithEnvironment(
                        ContainerEnvNames.NginxHttpsPortSuffix,
                        edgeHttpsPort == 443 ? string.Empty : $":{edgeHttpsPort}");
            }

            edge = edge
                .WithEnvironment(ContainerEnvNames.NginxServerName, edgeServerName)
                .WithEnvironment(ContainerEnvNames.NginxApiServerName, edgeApiHost)
                .WithEnvironment(ContainerEnvNames.NginxWebServerName, edgeWebHost)
                .WithEnvironment(
                    ContainerEnvNames.NginxIncludeWeb,
                    includeWebUpstream ? "1" : string.Empty)
                .WithEnvironment(ContainerEnvNames.NginxEnvsubstFilter, "^NGINX_")
                .WithHttpHealthCheck("/nginx-health", endpointName: HttpEndpointName);

            if (apiResource is not null)
            {
                var apiEndpoint = apiResource.GetEndpoint(HttpEndpointName);
                // Container→container: TargetPort is the listen port on the docker network.
                // Container→host project: DCP rewrites Host to aspire.dev.internal (the
                // tunnel proxy), which does not listen on Kestrel's TargetPort — use Port.
                var apiPortProperty = apiIsContainer
                    ? EndpointProperty.TargetPort
                    : EndpointProperty.Port;
                edge = edge
                    .WithEnvironment(
                        ContainerEnvNames.NginxApiUpstream,
                        ReferenceExpression.Create(
                            $"{apiEndpoint.Property(EndpointProperty.Host)}:{apiEndpoint.Property(apiPortProperty)}"))
                    .WaitFor(apiResource);
            }
            else
            {
                edge = edge.WithEnvironment(
                    ContainerEnvNames.NginxApiUpstream,
                    ComposeServices.InterfoldApi + ":" + apiContainerHttpPort.ToString());
            }

            if (web is not null)
            {
                var webEndpoint = web.GetEndpoint(HttpEndpointName);
                edge = edge
                    .WithEnvironment(
                        ContainerEnvNames.NginxWebUpstream,
                        ReferenceExpression.Create(
                            $"{webEndpoint.Property(EndpointProperty.Host)}:{webEndpoint.Property(EndpointProperty.TargetPort)}"))
                    .WaitFor(web);
            }
            else
            {
                edge = edge.WithEnvironment(
                    ContainerEnvNames.NginxWebUpstream,
                    ComposeServices.InterfoldWeb + ":8080");
            }

            if (!edgeCloudflareTunnel)
                edge = edge.WithExternalHttpEndpoints();

            edge = edge.PublishAsDockerComposeService((_, service) =>
            {
                service.Networks = includeWebUpstream
                    ? [ComposeNetworks.EdgeApi, ComposeNetworks.EdgeWeb]
                    : [ComposeNetworks.EdgeApi];
                service.Healthcheck = ComposeHealthcheck.Cmd(
                    interval: "15s", timeout: "5s", retries: 5, startPeriod: "10s",
                    command: ["wget", "-qO-", "http://127.0.0.1/nginx-health"]);
                // Private origin: no host port publish even if Aspire allocated an ephemeral mapping.
                if (edgeCloudflareTunnel)
                    service.Ports = [];
            });

            if (edgeCloudflareTunnel)
            {
                var tokenPath = builder.Configuration[AppHostParameterKeys.EdgeCloudflareTunnelTokenPath]
                    ?? throw new InvalidOperationException(
                        $"Missing configuration for '{AppHostParameterKeys.EdgeCloudflareTunnelTokenPath}'.");

                // --token-file landed in cloudflared 2025.4.0.
                var cloudflared = builder.AddContainer(ComposeServices.Cloudflared, "cloudflare/cloudflared", "2026.9.1")
                    .PullAlwaysInRunMode()
                    .WithArgs(
                        "tunnel", "--no-autoupdate", "run",
                        "--token-file", EdgePaths.ContainerCloudflareTunnelToken)
                    .WithBindMount(tokenPath, EdgePaths.ContainerCloudflareTunnelToken, isReadOnly: true)
                    .WaitFor(edge)
                    .PublishAsDockerComposeService((_, service) =>
                    {
                        service.Networks = includeWebUpstream
                            ? [ComposeNetworks.EdgeApi, ComposeNetworks.EdgeWeb]
                            : [ComposeNetworks.EdgeApi];
                        service.Restart = "unless-stopped";
                    });
                _ = cloudflared;
            }

            _ = edge;
        }
    }

    private static Action<Aspire.Hosting.Docker.DockerComposeServiceResource, Aspire.Hosting.Docker.Resources.ComposeNodes.Service> ApiComposeServicePublisher(int apiContainerHttpPort)
    {
        return (_, service) =>
        {
            service.Networks = [ComposeNetworks.EdgeApi];
            service.Healthcheck = ComposeHealthcheck.Cmd(
                interval: "15s", timeout: "5s", retries: 10, startPeriod: "20s",
                command: ["curl", "-f", $"http://localhost:{apiContainerHttpPort}{HealthEndpoints.Ready}"]);
        };
    }
}
