using System.Text.Json.Serialization;
using Interfold.Journals.Contracts.Models.Read;
using Interfold.Shared.Contracts;
using Interfold.Socket.Contracts;

namespace Interfold.Journals.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(JournalReadModel))]
[JsonSerializable(typeof(AlterJournalReadModel))]
[JsonSerializable(typeof(IReadOnlyList<JournalReadModel>))]
[JsonSerializable(typeof(IEnumerable<AlterJournalReadModel>))]
[JsonSerializable(typeof(CreateGlobalJournalRequest))]
[JsonSerializable(typeof(UpdateGlobalJournalRequest))]
[JsonSerializable(typeof(DeleteGlobalJournalRequest))]
[JsonSerializable(typeof(JournalActionRequest))]
[JsonSerializable(typeof(JournalAlterRequest))]
[JsonSerializable(typeof(CreateAlterJournalRequest))]
[JsonSerializable(typeof(UpdateAlterJournalRequest))]
[JsonSerializable(typeof(GlobalJournalSocketPayload))]
[JsonSerializable(typeof(AlterJournalSocketPayload))]
[JsonSerializable(typeof(EntryDeletedSocketPayload))]
[JsonSerializable(typeof(PhxFrame<GlobalJournalSocketPayload>))]
[JsonSerializable(typeof(PhxFrame<AlterJournalSocketPayload>))]
[JsonSerializable(typeof(PhxFrame<EntryDeletedSocketPayload>))]
public sealed partial class JournalsContractsJsonContext : JsonSerializerContext;
