using System.Text.Json.Serialization;
using Interfold.Ops.Api.Models;
using Interfold.Shared.Contracts;

namespace Interfold.Ops.Api.Json;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(OtlpDiscoveryResponse))]
public sealed partial class OpsApiJsonContext : JsonSerializerContext;
