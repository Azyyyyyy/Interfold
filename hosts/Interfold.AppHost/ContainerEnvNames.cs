namespace Interfold.AppHost;

/// <summary>Env-var names the AppHost graph sets on third-party containers.</summary>
internal static class ContainerEnvNames
{
    // --- Interfold API container (ASP.NET Core hosting + app secrets) ---
    public const string EncryptionPrivateKey = "ENCRYPTION_PRIVATE_KEY";
    public const string AspNetCoreHttpPorts = "ASPNETCORE_HTTP_PORTS";
    public const string AspNetCoreKestrelDefaultCertPath = "ASPNETCORE_Kestrel__Certificates__Default__Path";

    // --- Edge nginx (official nginx image envsubst-on-templates entrypoint) ---
    public const string NginxServerName = "NGINX_SERVER_NAME";
    public const string NginxApiServerName = "NGINX_API_SERVER_NAME";
    public const string NginxWebServerName = "NGINX_WEB_SERVER_NAME";
    public const string NginxSslCertFile = "NGINX_SSL_CERT_FILE";
    public const string NginxSslKeyFile = "NGINX_SSL_KEY_FILE";
    public const string NginxHttpsPortSuffix = "NGINX_HTTPS_PORT_SUFFIX";
    public const string NginxEnvsubstFilter = "NGINX_ENVSUBST_FILTER";
    public const string NginxApiUpstream = "NGINX_API_UPSTREAM";
    public const string NginxWebUpstream = "NGINX_WEB_UPSTREAM";
    public const string NginxIncludeWeb = "NGINX_INCLUDE_WEB";

    // Web image (nginx envsubst into runtime-config.js). Name is fixed by the image.
    public const string InterfoldDefaultApiEndpoint = "INTERFOLD_DEFAULT_API_ENDPOINT";
}
