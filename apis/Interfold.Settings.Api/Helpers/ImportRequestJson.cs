using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Interfold.Settings.Contracts;
using Interfold.Settings.Contracts.Models.Read;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;

namespace Interfold.Settings.Api.Helpers;

public static class ImportRequestJson
{
    public static bool TryDeserialize(
        JsonElement payload,
        [NotNullWhen(true)] out SettingsImportRequest? request,
        [NotNullWhen(false)] out ErrorResponse? error)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            request = null;
            error = new ErrorResponse("The request payload was invalid.", ErrorCodes.BadRequest, HttpStatusCode.BadRequest);
            return false;
        }

        try
        {
            request = payload.Deserialize(SettingsContractsJsonContext.Default.SettingsImportRequest);
            if (request is null)
            {
                error = new ErrorResponse("Import payload cannot be empty.", ErrorCodes.BadRequest, HttpStatusCode.BadRequest);
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException)
        {
            request = null;
            error = new ErrorResponse("The request payload was invalid.", ErrorCodes.BadRequest, HttpStatusCode.BadRequest);
            return false;
        }
    }
}
