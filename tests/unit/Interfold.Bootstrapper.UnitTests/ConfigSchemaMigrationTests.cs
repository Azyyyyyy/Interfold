using System.Text.Json;

using Interfold.Bootstrapper.Cli;

using Interfold.Bootstrapper.Configuration;

using Interfold.Bootstrapper.Phases;



namespace Interfold.Bootstrapper.UnitTests;



public sealed class ConfigSchemaMigrationTests

{

    private static readonly string FixtureDir = Path.Combine(

        AppContext.BaseDirectory,

        "..", "..", "..", "Fixtures");



    private static PhaseLogger Logger() => new(TestSupport.MakeOptions(outputDir: Path.GetTempPath()));



    private static ConfigSchemaMigrator.MigrationResult Migrate(string json, string path = "interfold.bootstrap.json")

        => ConfigSchemaMigrator.MigrateIfNeeded(json, path, Logger());



    private static BootstrapConfig DeserializeMigrated(string json)

    {

        var config = JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.BootstrapConfig)

            ?? throw new InvalidOperationException("deserialize returned null");

        ConfigPhase.ResolveDerivedDefaults(config);

        return config;

    }



    [Test]

    public async Task MinimalV1MigratesToV2()

    {

        var v1 = await File.ReadAllTextAsync(Path.Combine(FixtureDir, "interfold.bootstrap.v1.sample.json"));

        var result = Migrate(v1);

        await Assert.That(result.DidMigrate).IsTrue();



        using var doc = JsonDocument.Parse(result.Json);

        var root = doc.RootElement;

        await Assert.That(root.GetProperty("schemaVersion").GetInt32()).IsEqualTo(2);

        await Assert.That(root.GetProperty("edge").GetProperty("ports").GetProperty("http").GetInt32()).IsEqualTo(5000);

        await Assert.That(root.GetProperty("edge").GetProperty("ports").GetProperty("https").GetInt32()).IsEqualTo(5001);

        await Assert.That(root.GetProperty("edge").GetProperty("hosts")[0].GetString()).IsEqualTo("api.example.com");
        await Assert.That(root.GetProperty("deployment").GetProperty("includeWeb").GetBoolean()).IsFalse();

        await Assert.That(root.TryGetProperty("datastores", out var datastores) && datastores.ValueKind == JsonValueKind.Object).IsTrue();

        await Assert.That(root.TryGetProperty("ports", out _)).IsFalse();

        await Assert.That(root.GetProperty("deployment").TryGetProperty("edge", out _)).IsFalse();

        await Assert.That(root.GetProperty("deployment").TryGetProperty("webHttps", out _)).IsFalse();

    }



    [Test]

    public async Task V1WebHttpsPromotesIncludeWeb()

    {

        const string json = """

            {

              "deployment": { "hosts": ["a.example.com"], "includeWeb": false, "webHttps": true },

              "ports": { "apiHttp": 5000, "apiHttps": 5001, "webHttps": 8081 }

            }

            """;

        var result = Migrate(json);

        using var doc = JsonDocument.Parse(result.Json);

        await Assert.That(doc.RootElement.GetProperty("deployment").GetProperty("includeWeb").GetBoolean()).IsTrue();

    }



    [Test]

    public async Task V1IncludeWebTrueStaysTrueWhenWebHttpsFalse()

    {

        const string json = """

            {

              "deployment": { "hosts": ["a.example.com"], "includeWeb": true, "webHttps": false },

              "ports": { "apiHttp": 5000, "apiHttps": 5001 }

            }

            """;

        var result = Migrate(json);

        using var doc = JsonDocument.Parse(result.Json);

        await Assert.That(doc.RootElement.GetProperty("deployment").GetProperty("includeWeb").GetBoolean()).IsTrue();

    }



    [Test]

    public async Task V1CustomPortsPreservedOnEdge()

    {

        const string json = """

            {

              "deployment": { "hosts": ["a.example.com"] },

              "ports": { "apiHttp": 5100, "apiHttps": 5101 }

            }

            """;

        var result = Migrate(json);

        using var doc = JsonDocument.Parse(result.Json);

        await Assert.That(doc.RootElement.GetProperty("edge").GetProperty("ports").GetProperty("http").GetInt32()).IsEqualTo(5100);

        await Assert.That(doc.RootElement.GetProperty("edge").GetProperty("ports").GetProperty("https").GetInt32()).IsEqualTo(5101);

    }



    [Test]

    public async Task AlreadyV2IsNoOp()

    {

        const string json = """

            {

              "schemaVersion": 2,

              "deployment": { "includeWeb": false },

              "edge": {

                "hosts": ["a.example.com"],

                "tlsMode": "privateCa",

                "routing": { "mode": "path", "apiHost": "", "webHost": "" },

                "ports": { "http": 80, "https": 443 }

              },

              "datastores": {

                "postgres": { "database": "interfold" },

                "cql": { "backend": "scylla-single", "clusterName": "InterfoldCluster", "keyspace": "nam" }

              }

            }

            """;

        var result = Migrate(json);

        await Assert.That(result.DidMigrate).IsFalse();

        await Assert.That(result.Json).IsEqualTo(json);

    }



    [Test]

    public async Task V2EdgeEnabledFalseRejected()

    {

        const string json = """

            {

              "schemaVersion": 2,

              "deployment": {

                "hosts": ["a.example.com"],

                "edge": { "tlsMode": "privateCa", "routing": "path", "enabled": false }

              },

              "ports": { "edgeHttp": 80, "edgeHttps": 443 }

            }

            """;

        var ex = Assert.Throws<InvalidOperationException>(() => Migrate(json));

        await Assert.That(ex.Message).Contains("edge.enabled");

    }



    [Test]

    public async Task MigrationNoticeShowsNewUrlsAndWasLineWhenWebPortChanged()

    {

        const string json = """

            {

              "deployment": { "hosts": ["api.example.com"], "includeWeb": true, "webHttps": true },

              "ports": { "apiHttp": 5000, "apiHttps": 5001, "webHttps": 8081 }

            }

            """;

        var result = Migrate(json);

        var config = DeserializeMigrated(result.Json);

        var lines = PublicEndpointUrls.FormatMigrationNotice(config, result.V1Snapshot, "interfold.bootstrap.json");



        var text = string.Join('\n', lines);

        await Assert.That(text).Contains("API:  https://api.example.com:5001/api/");

        await Assert.That(text).Contains("Web:  https://api.example.com:5001/");

        await Assert.That(text).Contains("(was: API https://api.example.com:5001/, web https://api.example.com:8081/)");

    }



    [Test]

    public async Task MigrationNoticeOmitsWebWhenDisabled()

    {

        const string json = """

            {

              "deployment": { "hosts": ["api.example.com"], "includeWeb": false },

              "ports": { "apiHttp": 5000, "apiHttps": 5001, "webHttps": 8081 }

            }

            """;

        var result = Migrate(json);

        var config = DeserializeMigrated(result.Json);

        var lines = PublicEndpointUrls.FormatMigrationNotice(config, result.V1Snapshot, "interfold.bootstrap.json");



        var text = string.Join('\n', lines);

        await Assert.That(text).Contains("API:  https://api.example.com:5001/api/");

        await Assert.That(text).DoesNotContain("Web:");

        await Assert.That(text).DoesNotContain("(was:");

    }



    [Test]

    public async Task DetectVersionTreatsMissingAsV1()

    {

        const string json = """{ "deployment": { "hosts": ["a.example.com"] } }""";

        using var doc = JsonDocument.Parse(json);

        await Assert.That(ConfigSchemaMigrator.DetectVersion(doc.RootElement)).IsEqualTo(1);

    }

}

