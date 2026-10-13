using System.Text.Json.Serialization;
using Interfold.Settings.Contracts.Ids;

namespace Interfold.Settings.Contracts.Models.Commands;

public sealed record ImportPkCommand(ImportToken Token);

public sealed record ImportSpCommand(ImportToken Token, RecoveryCode? RecoveryCode);

public sealed record ImportOctoconCommand(string RawPayload);

public sealed record SpImportPayload(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("recovery_code")] string? RecoveryCode);

public sealed record PkImportPayload(
    [property: JsonPropertyName("token")] string Token);
