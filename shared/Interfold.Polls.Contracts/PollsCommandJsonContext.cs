using System.Text.Json.Serialization;
using Interfold.Polls.Contracts.Models.Commands;

namespace Interfold.Polls.Contracts;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CreatePollCommand))]
[JsonSerializable(typeof(UpdatePollCommand))]
[JsonSerializable(typeof(DeletePollCommand))]
[JsonSerializable(typeof(PollCommandResult))]
public sealed partial class PollsCommandJsonContext : JsonSerializerContext;
