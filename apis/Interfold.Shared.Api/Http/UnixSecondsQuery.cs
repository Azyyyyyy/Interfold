using System.Net;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;

namespace Interfold.Shared.Api.Http;

/// <summary>Query-string Unix-seconds bind that preserves the Fronting wire codes
/// (<c>invalid_end_anchor</c> / <c>invalid_anchor</c>) without MVC ModelState.</summary>
public readonly record struct UnixSecondsQuery(UnixSeconds Value, ErrorResponse? Error)
{
    public bool IsError => Error is not null;

    public static UnixSecondsQuery Bind(HttpContext http, string queryName, string errorCode, string errorMessage)
    {
        if (!http.Request.Query.TryGetValue(queryName, out var raw)
            || !UnixSeconds.TryParse(raw.ToString(), provider: null, out var parsed))
        {
            return new UnixSecondsQuery(default, new ErrorResponse(errorMessage, new ErrorCode(errorCode), HttpStatusCode.BadRequest));
        }

        return new UnixSecondsQuery(parsed, null);
    }
}
