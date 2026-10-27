using System.Text.Json.Serialization;
using Interfold.Shared.Contracts;
using Interfold.Socket.Contracts;
using Interfold.Tags.Contracts.Models.Read;

namespace Interfold.Tags.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(TagReadModel))]
[JsonSerializable(typeof(TagPublicReadModel))]
[JsonSerializable(typeof(IReadOnlyList<TagReadModel>))]
[JsonSerializable(typeof(IReadOnlyList<TagPublicReadModel>))]
[JsonSerializable(typeof(CreateTagRequest))]
[JsonSerializable(typeof(UpdateTagRequest))]
[JsonSerializable(typeof(TagAlterRequest))]
[JsonSerializable(typeof(SetParentRequest))]
[JsonSerializable(typeof(SocketBatchedTagsPayload))]
[JsonSerializable(typeof(TagSocketPayload))]
[JsonSerializable(typeof(TagDeletedSocketPayload))]
[JsonSerializable(typeof(PhxFrame<SocketBatchedTagsPayload>))]
[JsonSerializable(typeof(PhxFrame<TagSocketPayload>))]
[JsonSerializable(typeof(PhxFrame<TagDeletedSocketPayload>))]
public sealed partial class TagsContractsJsonContext : JsonSerializerContext;
