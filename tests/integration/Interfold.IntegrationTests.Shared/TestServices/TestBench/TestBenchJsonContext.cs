using System.Text.Json.Serialization;

namespace Interfold.IntegrationTests.Shared.TestServices.TestBench;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TestBenchLeaseState))]
[JsonSerializable(typeof(TestBenchUser))]
[JsonSerializable(typeof(BenchPorts))]
[JsonSerializable(typeof(List<TestBenchUser>))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(Dictionary<string, TestBenchUser>))]
internal sealed partial class TestBenchJsonContext : JsonSerializerContext;
