using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// The TimeUuid surrogate key of an <c>import_operations</c> row — also the public
/// correlation handle returned to the HTTP caller and echoed in socket frames. Distinct
/// from <see cref="OperationId"/> (the string-backed command-vocabulary id like
/// <c>cmd.settings.import.sp</c>); the two used to share a name and were easy to cross-wire.
///
/// <para>JSON serializes as the standard <c>"D"</c>-format GUID string — identical to the
/// previous raw <see cref="Guid"/> serialization, so persisted command results and HTTP
/// bodies are unchanged.</para>
/// </summary>
[JsonConverter(typeof(ImportOperationIdJsonConverter))]
public readonly record struct ImportOperationId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

internal sealed class ImportOperationIdJsonConverter : JsonConverter<ImportOperationId>
{
    public override ImportOperationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, ImportOperationId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
