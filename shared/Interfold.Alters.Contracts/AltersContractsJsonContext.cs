using System.Text.Json.Serialization;
using Interfold.Alters.Contracts.Models;
using Interfold.Alters.Contracts.Models.Commands;
using Interfold.Alters.Contracts.Models.Read;
using Interfold.Shared.Contracts;
using Interfold.Socket.Contracts;

namespace Interfold.Alters.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(BareAlter))]
[JsonSerializable(typeof(AlterReadModel))]
[JsonSerializable(typeof(IReadOnlyList<BareAlter>))]
[JsonSerializable(typeof(IReadOnlyList<AlterReadModel>))]
[JsonSerializable(typeof(CreateAlterRequest))]
[JsonSerializable(typeof(UpdateAlterRequest))]
[JsonSerializable(typeof(UpdateAlterFieldRequest))]
[JsonSerializable(typeof(AlterSocketPayload))]
[JsonSerializable(typeof(AlterDeletedSocketPayload))]
[JsonSerializable(typeof(AlterIdSocketPayload))]
[JsonSerializable(typeof(PhxFrame<AlterSocketPayload>))]
[JsonSerializable(typeof(PhxFrame<AlterDeletedSocketPayload>))]
[JsonSerializable(typeof(PhxFrame<AlterIdSocketPayload>))]
public sealed partial class AltersContractsJsonContext : JsonSerializerContext;
