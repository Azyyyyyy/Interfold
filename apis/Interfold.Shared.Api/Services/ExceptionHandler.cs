using System.Net;
using System.Text.Json;
using Interfold.Shared.Api.Json;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Microsoft.AspNetCore.Diagnostics;

namespace Interfold.Shared.Api.Services;

public class ExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Log the exception and return a generic error response.
        httpContext.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
            httpContext.Response.Body,
            CreateError(httpContext, exception),
            SharedApiJsonContext.Default.ErrorResponse,
            cancellationToken);
        return true;
    }

    private ErrorResponse CreateError(HttpContext httpContext, Exception exception)
    {
        if (exception is InterfoldException interfoldException)
        {
            httpContext.Response.StatusCode = (int?)interfoldException.HttpStatusCode ?? StatusCodes.Status500InternalServerError;
            return new ErrorResponse(interfoldException.Message, interfoldException.Code);
        }

        if (exception is BadHttpRequestException badRequest)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return new ErrorResponse(badRequest.Message, ErrorCodes.BadRequest, HttpStatusCode.BadRequest);
        }

        if (exception is JsonException jsonException)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return new ErrorResponse(jsonException.Message, ErrorCodes.BadRequest, HttpStatusCode.BadRequest);
        }

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return new ErrorResponse($"Unhandled exception: {exception}", ErrorCodes.UnknownError);
    }
}
