using System.Text.Json.Serialization;
using Interfold.Journals.Contracts.Models.Commands;

namespace Interfold.Journals.Contracts;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CreateGlobalJournalEntryCommand))]
[JsonSerializable(typeof(UpdateGlobalJournalEntryCommand))]
[JsonSerializable(typeof(DeleteGlobalJournalEntryCommand))]
[JsonSerializable(typeof(SetGlobalJournalLockedCommand))]
[JsonSerializable(typeof(SetGlobalJournalPinnedCommand))]
[JsonSerializable(typeof(AttachAlterToGlobalJournalCommand))]
[JsonSerializable(typeof(DetachAlterFromGlobalJournalCommand))]
[JsonSerializable(typeof(CreateAlterJournalEntryCommand))]
[JsonSerializable(typeof(UpdateAlterJournalEntryCommand))]
[JsonSerializable(typeof(DeleteAlterJournalEntryCommand))]
[JsonSerializable(typeof(SetAlterJournalLockedCommand))]
[JsonSerializable(typeof(SetAlterJournalPinnedCommand))]
[JsonSerializable(typeof(GlobalJournalCommandResult))]
[JsonSerializable(typeof(AlterJournalCommandResult))]
public sealed partial class JournalsCommandJsonContext : JsonSerializerContext;
