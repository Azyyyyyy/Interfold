using System.Text.Json.Serialization;
using Interfold.Tags.Contracts.Models.Commands;

namespace Interfold.Tags.Contracts;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CreateTagCommand))]
[JsonSerializable(typeof(UpdateTagCommand))]
[JsonSerializable(typeof(DeleteTagCommand))]
[JsonSerializable(typeof(AttachAlterToTagCommand))]
[JsonSerializable(typeof(DetachAlterFromTagCommand))]
[JsonSerializable(typeof(SetParentTagCommand))]
[JsonSerializable(typeof(RemoveParentTagCommand))]
[JsonSerializable(typeof(TagCommandResult))]
public sealed partial class TagsCommandJsonContext : JsonSerializerContext;
