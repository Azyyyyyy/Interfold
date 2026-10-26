using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.IntegrationTests.Shared.Models;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(ReplayTrace))]
[JsonSerializable(typeof(ReplayStep))]
[JsonSerializable(typeof(List<ReplayStep>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class ReplayTraceJsonContext : JsonSerializerContext;
