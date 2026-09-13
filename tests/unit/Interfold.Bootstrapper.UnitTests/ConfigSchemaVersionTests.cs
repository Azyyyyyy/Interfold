using System.Text.Json;
using Interfold.Bootstrapper.Configuration;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class ConfigSchemaVersionTests
{
    [Test]
    public async Task LegacyWireTwoIsTwoDotZeroAndWritesTwo()
    {
        var version = ConfigSchemaVersion.FromWire(2);
        await Assert.That(version).IsEqualTo(ConfigSchemaVersion.V2);
        await Assert.That(version.ToWire()).IsEqualTo(2);
        await Assert.That(version.ToString()).IsEqualTo("2");
    }

    [Test]
    public async Task WireTwentyOneIsTwoDotOne()
    {
        var version = ConfigSchemaVersion.FromWire(21);
        await Assert.That(version.Major).IsEqualTo(2);
        await Assert.That(version.Minor).IsEqualTo(1);
        await Assert.That(version.ToWire()).IsEqualTo(21);
        await Assert.That(version.ToString()).IsEqualTo("2.1");
    }

    [Test]
    public async Task DecimalTwoDotOneParsesAsMinorBump()
    {
        var version = ConfigSchemaVersion.Parse(2.1m);
        await Assert.That(version).IsEqualTo(new ConfigSchemaVersion(2, 1));
        await Assert.That(version.ToWire()).IsEqualTo(21);
    }

    [Test]
    public async Task TwoDotZeroIsLessThanTwoDotOneAndLessThanThree()
    {
        await Assert.That(ConfigSchemaVersion.V2 < new ConfigSchemaVersion(2, 1)).IsTrue();
        await Assert.That(new ConfigSchemaVersion(2, 1) < new ConfigSchemaVersion(3, 0)).IsTrue();
    }

    [Test]
    public async Task BootstrapConfigRoundTripsLegacyTwoAsInteger()
    {
        var json = JsonSerializer.Serialize(new BootstrapConfig(), BootstrapJsonContext.Default.BootstrapConfig);
        using var doc = JsonDocument.Parse(json);
        var prop = doc.RootElement.GetProperty("schemaVersion");
        await Assert.That(prop.ValueKind).IsEqualTo(JsonValueKind.Number);
        await Assert.That(prop.GetInt32()).IsEqualTo(2);

        var loaded = JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.BootstrapConfig);
        await Assert.That(loaded!.SchemaVersion).IsEqualTo(ConfigSchemaVersion.V2);
    }

    [Test]
    public async Task BootstrapConfigReadsWireMinorAndDecimalLiteral()
    {
        const string wireJson = """{ "schemaVersion": 21 }""";
        const string decimalJson = """{ "schemaVersion": 2.1 }""";

        var fromWire = JsonSerializer.Deserialize(wireJson, BootstrapJsonContext.Default.BootstrapConfig);
        var fromDecimal = JsonSerializer.Deserialize(decimalJson, BootstrapJsonContext.Default.BootstrapConfig);
        var expected = new ConfigSchemaVersion(2, 1);

        await Assert.That(fromWire!.SchemaVersion).IsEqualTo(expected);
        await Assert.That(fromDecimal!.SchemaVersion).IsEqualTo(expected);

        var written = JsonSerializer.Serialize(
            new BootstrapConfig { SchemaVersion = expected },
            BootstrapJsonContext.Default.BootstrapConfig);
        using var doc = JsonDocument.Parse(written);
        await Assert.That(doc.RootElement.GetProperty("schemaVersion").GetInt32()).IsEqualTo(21);
    }

    [Test]
    public async Task DetectVersionReadsWireMinorAndDoesNotMigrateNewerFile()
    {
        const string json = """
            {
              "schemaVersion": 21,
              "deployment": { "includeWeb": false },
              "edge": {
                "hosts": ["a.example.com"],
                "tlsMode": "privateCa",
                "routing": { "mode": "path", "apiHost": "", "webHost": "" },
                "ports": { "http": 80, "https": 443 }
              }
            }
            """;

        using var doc = JsonDocument.Parse(json);
        await Assert.That(ConfigSchemaMigrator.DetectVersion(doc.RootElement))
            .IsEqualTo(new ConfigSchemaVersion(2, 1));

        var result = ConfigSchemaMigrator.MigrateIfNeeded(
            json,
            "interfold.bootstrap.json",
            new Interfold.Bootstrapper.Cli.PhaseLogger(
                TestSupport.MakeOptions(outputDir: Path.GetTempPath())));
        await Assert.That(result.DidMigrate).IsFalse();
    }
}
