using System.Text.Json.Serialization;
using Interfold.Alters.Contracts.Models;
using Interfold.Shared.Contracts;
using Interfold.Systems.Contracts.Models.Read;

namespace Interfold.Systems.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(PublicSystemReadModel))]
[JsonSerializable(typeof(PublicSystemBatchReadModel))]
[JsonSerializable(typeof(BareAlter))]
[JsonSerializable(typeof(IReadOnlyList<BareAlter>))]
public sealed partial class SystemsContractsJsonContext : JsonSerializerContext;
