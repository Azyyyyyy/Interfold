using System.Text.Json.Serialization;
using Interfold.Fronting.Contracts.Models.Commands;

namespace Interfold.Fronting.Contracts;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StartFrontCommand))]
[JsonSerializable(typeof(EndFrontCommand))]
[JsonSerializable(typeof(SetPrimaryFrontCommand))]
[JsonSerializable(typeof(FrontStartItem))]
[JsonSerializable(typeof(BulkUpdateFrontCommand))]
[JsonSerializable(typeof(SetFrontCommand))]
[JsonSerializable(typeof(DeleteFrontByIdCommand))]
[JsonSerializable(typeof(UpdateFrontCommentCommand))]
[JsonSerializable(typeof(FrontCommandResult))]
public sealed partial class FrontingCommandJsonContext : JsonSerializerContext;
