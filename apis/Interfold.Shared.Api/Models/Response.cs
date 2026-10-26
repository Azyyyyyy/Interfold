using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Interfold.Shared.Api.Json;
using Microsoft.Extensions.Options;
using OneOf;

namespace Interfold.Shared.Api.Models;

public class Response : Response<NoContent>
{
    public Response() : base(new SuccessResponse<NoContent>(new NoContent(), HttpStatusCode.NoContent))
    {
    }
    
    public Response(ErrorResponse response) : base(response)
    {
    }
    
    /// <summary>
    /// Implicitly converts the specified <paramref name="error"/> to an <see cref="Response{TValue}"/>.
    /// </summary>
    /// <param name="error">The error to convert.</param>
    public static implicit operator Response(ErrorResponse error)
    {
        return new Response(error);
    }
}

[GenerateOneOf]
public class Response<TValue> : OneOfBase<SuccessResponse<TValue>, ErrorResponse>, IResult, IStatusCodeHttpResult
{
    public Response(OneOf<SuccessResponse<TValue>, ErrorResponse> input) : base(input)
    {
    }

    public int? StatusCode => IsSuccess ? (int)AsSuccess.StatusCode : (int)AsError.StatusCode;

    /// <summary>
    /// Implicitly converts the specified <paramref name="value"/> to an <see cref="Response{TValue}"/>.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    public static implicit operator Response<TValue>(TValue value)
    {
        return new Response<TValue>(new SuccessResponse<TValue>(value));
    }
    
    /// <summary>
    /// Implicitly converts the specified <paramref name="value"/> to an <see cref="Response{TValue}"/>.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    public static implicit operator Response<TValue>(SuccessResponse<TValue> value)
    {
        return new Response<TValue>(value);
    }
    
    /// <summary>
    /// Implicitly converts the specified <paramref name="error"/> to an <see cref="Response{TValue}"/>.
    /// </summary>
    /// <param name="error">The error to convert.</param>
    public static implicit operator Response<TValue>(ErrorResponse error)
    {
        return new Response<TValue>(error);
    }
    
    public bool IsSuccess => IsT0;
    public SuccessResponse<TValue> AsSuccess => AsT0;
    public bool IsError => IsT1;
    public ErrorResponse AsError => AsT1;

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        if (IsSuccess)
        {
            var statusCode = (int)AsSuccess.StatusCode;
            httpContext.Response.StatusCode = statusCode;
            if (statusCode == StatusCodes.Status204NoContent)
                return;

            httpContext.Response.ContentType = "application/json";
            var typeInfo = ResolveTypeInfo<SuccessResponse<TValue>>(httpContext);
            await JsonSerializer.SerializeAsync(httpContext.Response.Body, AsSuccess, typeInfo, httpContext.RequestAborted);
            return;
        }

        httpContext.Response.StatusCode = (int)AsError.StatusCode;
        httpContext.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
            httpContext.Response.Body,
            AsError,
            SharedApiJsonContext.Default.ErrorResponse,
            httpContext.RequestAborted);
    }

    private static JsonTypeInfo<T> ResolveTypeInfo<T>(HttpContext httpContext)
    {
        var options = httpContext.RequestServices
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value.SerializerOptions;
        var resolver = options.TypeInfoResolver
            ?? throw new InvalidOperationException("HTTP JSON TypeInfoResolver is not configured.");
        var info = resolver.GetTypeInfo(typeof(T), options)
            ?? throw new InvalidOperationException($"Missing JsonTypeInfo for {typeof(T)}.");
        return (JsonTypeInfo<T>)info;
    }
}
