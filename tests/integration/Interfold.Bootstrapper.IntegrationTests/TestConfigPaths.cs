namespace Interfold.Bootstrapper.IntegrationTests;

public static class TestConfigPaths
{
    public static string DefaultConfig => Path.Combine(AppContext.BaseDirectory, "Fixtures", "interfold.bootstrap.test.json");
    public static string TrustInstallConfig => Path.Combine(AppContext.BaseDirectory, "Fixtures", "interfold.bootstrap.test.trust-install.json");
    public static string CassandraConfig => Path.Combine(AppContext.BaseDirectory, "Fixtures", "interfold.bootstrap.test.cassandra.json");
    public static string BadImageConfig => Path.Combine(AppContext.BaseDirectory, "Fixtures", "interfold.bootstrap.test.bad-image.json");
    public static string MdnsGateConfig => Path.Combine(AppContext.BaseDirectory, "Fixtures", "interfold.bootstrap.test.mdns-gate.json");
    public static string EdgeConfig => Path.Combine(AppContext.BaseDirectory, "Fixtures", "interfold.bootstrap.test.edge.json");
    public static string EdgeHttpConfig => Path.Combine(AppContext.BaseDirectory, "Fixtures", "interfold.bootstrap.test.edge-http.json");
    public static string CloudflareTunnelConfig => Path.Combine(AppContext.BaseDirectory, "Fixtures", "interfold.bootstrap.test.cloudflare-tunnel.json");
}
