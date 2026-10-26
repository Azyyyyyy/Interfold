using System.Text.Json.Serialization;
using Interfold.Settings.Api.Services.Export;

namespace Interfold.Settings.Api.Json;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PkExportPayload))]
[JsonSerializable(typeof(PkMember))]
[JsonSerializable(typeof(PkProxyTag))]
[JsonSerializable(typeof(PkGroup))]
[JsonSerializable(typeof(OctoconExportPayload))]
[JsonSerializable(typeof(OctoconExportUser))]
[JsonSerializable(typeof(OctoconExportUserField))]
[JsonSerializable(typeof(OctoconExportAlter))]
[JsonSerializable(typeof(OctoconExportAlterField))]
[JsonSerializable(typeof(OctoconExportFront))]
[JsonSerializable(typeof(OctoconExportTag))]
[JsonSerializable(typeof(OctoconExportPoll))]
public sealed partial class ExportJsonContext : JsonSerializerContext;
