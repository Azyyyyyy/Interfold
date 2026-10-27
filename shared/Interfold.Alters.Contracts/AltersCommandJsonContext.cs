using System.Text.Json.Serialization;
using Interfold.Alters.Contracts.Models.Commands;

namespace Interfold.Alters.Contracts;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CreateAlterCommand))]
[JsonSerializable(typeof(UpdateAlterCommand))]
[JsonSerializable(typeof(DeleteAlterCommand))]
[JsonSerializable(typeof(AlterFieldCommand))]
[JsonSerializable(typeof(AlterCommandResult))]
public sealed partial class AltersCommandJsonContext : JsonSerializerContext;
