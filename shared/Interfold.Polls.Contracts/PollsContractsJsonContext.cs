using System.Text.Json.Serialization;
using Interfold.Polls.Contracts.Models;
using Interfold.Polls.Contracts.Models.Read;
using Interfold.Shared.Contracts;
using Interfold.Socket.Contracts;

namespace Interfold.Polls.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(PollReadModel))]
[JsonSerializable(typeof(IReadOnlyList<PollReadModel>))]
[JsonSerializable(typeof(CreatePollRequest))]
[JsonSerializable(typeof(UpdatePollRequest))]
[JsonSerializable(typeof(VotePollData))]
[JsonSerializable(typeof(ChoicePollData))]
[JsonSerializable(typeof(PollDataChoice))]
[JsonSerializable(typeof(PollDataResponse))]
[JsonSerializable(typeof(PollSocketPayload))]
[JsonSerializable(typeof(PollDeletedSocketPayload))]
[JsonSerializable(typeof(PhxFrame<PollSocketPayload>))]
[JsonSerializable(typeof(PhxFrame<PollDeletedSocketPayload>))]
public sealed partial class PollsContractsJsonContext : JsonSerializerContext;
