using System.Text.Json.Serialization;
using Interfold.Journals.Contracts.Models.Read;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;

namespace Interfold.Journals.Api.Json;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(SuccessResponse<JournalReadModel>))]
[JsonSerializable(typeof(SuccessResponse<IReadOnlyList<JournalReadModel>>))]
[JsonSerializable(typeof(SuccessResponse<AlterJournalReadModel>))]
[JsonSerializable(typeof(SuccessResponse<IEnumerable<AlterJournalReadModel>>))]
public sealed partial class JournalsApiJsonContext : JsonSerializerContext;
