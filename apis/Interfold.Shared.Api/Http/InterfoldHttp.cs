using System.Net;
using Interfold.Shared.Api.Helpers;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Api.Services;
using Interfold.Shared.Api.Uploads;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Contracts.Validation;
using Interfold.Shared.Domain.Abstractions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Interfold.Shared.Api.Http;

public static class InterfoldHttp
{
    // Synthetic stamp for envelopes built from AllowAnonymous endpoints (OAuth callbacks);
    // the handler resolves the real system id off the payload and must never trust this.
    private static readonly SystemId AnonymousPrincipalId = SystemId.Parse("auth", null);

    public static SystemId PrincipalId(this HttpContext http)
    {
        if (http.TryGetPrincipalId() is { } principal)
            return principal;

        throw new InvalidOperationException(
            "PrincipalId is unavailable. Ensure InterfoldPrincipalMiddleware is configured.");
    }

    public static SystemId? TryGetPrincipalId(this HttpContext http)
        => http.Items.TryGetValue(InterfoldHttpContextKeys.PrincipalId, out var value)
           && value is SystemId principal
            ? principal
            : null;

    public static TimeProvider Time(this HttpContext http)
        => http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>Header <c>X-Interfold-Idempotency-Key</c> if present, else a fresh GUID
    /// (unkeyed requests are each their own operation).</summary>
    public static IdempotencyKey GetIdempotencyKey(this HttpContext http)
    {
        var header = http.Request.Headers[InterfoldHeaders.IdempotencyKey].FirstOrDefault();
        return new(!string.IsNullOrWhiteSpace(header) ? header : Guid.NewGuid().ToString("N"));
    }

    public static ErrorResponse? RejectIfSelf(this HttpContext http, SystemId target, string message, ErrorCode code)
        => http.PrincipalId().RepresentsSameUserAs(target)
            ? new ErrorResponse(message, code, HttpStatusCode.BadRequest)
            : null;

    public static AvatarUrl? QualifyAvatar(this HttpContext http, IAvatarBearing? bearing)
        => AvatarUrlQualifier.QualifyAvatar(bearing, http.Request.Scheme, http.Request.Host);

    public static ErrorResponse? InvalidAlterId(AlterId id)
        => ValidAlterIdAttribute.IsValid(id)
            ? null
            : new ErrorResponse("Invalid alter ID.", ErrorCodes.InvalidAlterId, HttpStatusCode.BadRequest);

    public static ErrorResponse? InvalidAlterId(AlterId? id, bool allowNull = false)
    {
        if (id is null)
        {
            return allowNull
                ? null
                : new ErrorResponse("Invalid alter ID.", ErrorCodes.InvalidAlterId, HttpStatusCode.BadRequest);
        }

        return InvalidAlterId(id.Value);
    }

    public static CommandEnvelope<TPayload> BuildEnvelope<TPayload>(this HttpContext http, OperationId operationId, TPayload payload)
        => new(operationId, Guid.NewGuid(),
            PrincipalId: http.TryGetPrincipalId() ?? AnonymousPrincipalId,
            IdempotencyKey: http.GetIdempotencyKey(),
            OccurredAt: http.Time().GetUtcNow(), Payload: payload);

    public static async Task<Response> DispatchNoContentAsync<TPayload, TResult>(
        this HttpContext http,
        ICommandHandler<TPayload, TResult> handler,
        OperationId operationId,
        TPayload payload,
        CancellationToken ct)
        where TResult : ICommandResult
    {
        var envelope = http.BuildEnvelope(operationId, payload);
        var result = await handler.HandleAsync(envelope, ct);
        return http.CommandNoContent(result);
    }

    public static async Task<Response<TData>> DispatchCreatedAsync<TPayload, TResult, TData>(
        this HttpContext http,
        ICommandHandler<TPayload, TResult> handler,
        OperationId operationId,
        TPayload payload,
        Func<TResult, Task<TData?>> dataSelector,
        CancellationToken ct,
        Func<TResult, string>? locationSelector = null)
        where TResult : ICommandResult
    {
        var envelope = http.BuildEnvelope(operationId, payload);
        var result = await handler.HandleAsync(envelope, ct);
        return await http.CommandCreatedAsync(result, dataSelector, locationSelector);
    }

    public static async Task<Response<TData>> DispatchAcceptedAsync<TPayload, TResult, TData>(
        this HttpContext http,
        ICommandHandler<TPayload, TResult> handler,
        OperationId operationId,
        TPayload payload,
        Func<TResult, TData> dataSelector,
        CancellationToken ct)
        where TResult : ICommandResult
    {
        var envelope = http.BuildEnvelope(operationId, payload);
        var result = await handler.HandleAsync(envelope, ct);
        return http.CommandAccepted(result, dataSelector);
    }

    public static async Task<Response<TWire>> DispatchOkAsync<TPayload, TResult, TWire>(
        this HttpContext http,
        ICommandHandler<TPayload, TResult> handler,
        OperationId operationId,
        TPayload payload,
        Func<TResult, TWire> wireSelector,
        CancellationToken ct)
        where TResult : ICommandResult
    {
        var envelope = http.BuildEnvelope(operationId, payload);
        var result = await handler.HandleAsync(envelope, ct);
        if (result.Accepted)
            return new SuccessResponse<TWire>(wireSelector(result.Result!));

        return http.ConflictToError(result.Conflict!);
    }

    public static Response CommandNoContent<T>(this HttpContext http, CommandExecutionResult<T> result)
    {
        if (result.Accepted)
            return new Response();

        return http.ConflictToError(result.Conflict!);
    }

    public static Response<TData> CommandCreated<T, TData>(this HttpContext http, CommandExecutionResult<T> result, Func<T, TData> dataSelector)
        where T : ICommandResult
    {
        if (result.Accepted)
            return new SuccessResponse<TData>(dataSelector(result.Result!), HttpStatusCode.Created, result.Result!.Replay);

        return http.ConflictToError(result.Conflict!);
    }

    public static async Task<Response<TData>> CommandCreatedAsync<T, TData>(
        this HttpContext http,
        CommandExecutionResult<T> result,
        Func<T, Task<TData?>> dataSelector,
        Func<T, string>? locationSelector = null)
        where T : ICommandResult
    {
        if (!result.Accepted)
            return http.ConflictToError(result.Conflict!);

        var data = await dataSelector(result.Result!);
        if (data is null)
            return new ErrorResponse("An unknown error occurred.", ErrorCodes.UnknownError, HttpStatusCode.InternalServerError);

        if (locationSelector != null)
            http.Response.Headers.Location = locationSelector(result.Result!);

        return new SuccessResponse<TData>(data, HttpStatusCode.Created, result.Result!.Replay);
    }

    public static Response<TData> CommandAccepted<T, TData>(this HttpContext http, CommandExecutionResult<T> result, Func<T, TData> dataSelector)
    {
        if (result.Accepted)
            return new SuccessResponse<TData>(dataSelector(result.Result!), HttpStatusCode.Accepted);

        return http.ConflictToError(result.Conflict!);
    }

    public static ErrorResponse ConflictToError(this HttpContext http, ConflictResult conflict)
    {
        http.Response.Headers[InterfoldHeaders.OperationId] = conflict.OperationId.Value;

        return conflict.Code switch
        {
            ConflictCode.ConflictDuplicate => new ErrorResponse(
                "A duplicate conflict occurred.", new ErrorCode(conflict.ResolutionHint.ToWire()), HttpStatusCode.Conflict, conflict.EntityRef.Value),
            ConflictCode.ConflictInvariant => new ErrorResponse(
                "The request could not be processed due to a conflict.", new ErrorCode(conflict.ResolutionHint.ToWire()),
                HttpStatusCode.UnprocessableEntity, conflict.EntityRef.Value),
            _ => new ErrorResponse("An unknown error occurred.", ErrorCodes.UnknownError, HttpStatusCode.InternalServerError, conflict.EntityRef.Value)
        };
    }

    public static Response<T> OkOrNotFound<T>(T? value, string message, ErrorCode code)
        => value is null
            ? new ErrorResponse(message, code, HttpStatusCode.NotFound)
            : new SuccessResponse<T>(value);

    public static async Task<AvatarUploadPayload> ResolveMultipartUploadAsync(this HttpContext http, CancellationToken ct)
    {
        var emptyFilePart = false;
        var request = http.Request;

        if (request.Body is null)
            return new AvatarUploadPayload(null, emptyFilePart);

        request.EnableBuffering();

        if (request.Body.CanSeek)
            request.Body.Position = 0;

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.HasValue
            || !mediaType.MediaType.Value.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return new AvatarUploadPayload(null, emptyFilePart);
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
            return new AvatarUploadPayload(null, emptyFilePart);

        try
        {
            var reader = new MultipartReader(boundary, request.Body);
            MultipartSection? section;

            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                    continue;

                var fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value
                               ?? HeaderUtilities.RemoveQuotes(disposition.FileName).Value;

                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    var payload = new MemoryStream();
                    await section.Body.CopyToAsync(payload, ct);
                    if (payload.Length <= 0)
                    {
                        emptyFilePart = true;
                        await payload.DisposeAsync();
                        continue;
                    }

                    payload.Position = 0;
                    return new AvatarUploadPayload(payload, emptyFilePart);
                }
            }
        }
        catch (IOException)
        {
            return new AvatarUploadPayload(null, emptyFilePart);
        }

        return new AvatarUploadPayload(null, emptyFilePart);
    }

    public static async Task<Response> HandleAvatarUploadAsync(
        this HttpContext http,
        Func<CancellationToken, Task<IAvatarBearing?>> getExistingAvatarAsync,
        Func<SystemId, Stream, CancellationToken, Task<AvatarUrl>> saveToStorageAsync,
        Func<AvatarUrl, CancellationToken, Task<Response>> updateMetadataAsync,
        IAvatarStorage avatarStorage,
        CancellationToken ct)
    {
        var principal = http.PrincipalId();
        var upload = await http.ResolveMultipartUploadAsync(ct);
        var avatarStream = upload.Stream;
        if (avatarStream is null)
        {
            if (upload.EmptyFilePart)
                return new ErrorResponse("Avatar file is empty.", ErrorCodes.AvatarFileEmpty, HttpStatusCode.BadRequest);

            return new ErrorResponse("No avatar file provided.", ErrorCodes.AvatarFileRequired, HttpStatusCode.BadRequest);
        }

        AvatarUrl avatarUrl;
        try
        {
            await using (avatarStream)
            {
                avatarUrl = await saveToStorageAsync(principal, avatarStream, ct);
            }
        }
        catch
        {
            return new ErrorResponse("An error occurred while uploading the file.", ErrorCodes.UnknownError, HttpStatusCode.InternalServerError);
        }

        return await http.RunAvatarMetadataChangeAsync(
            getExistingAvatarAsync,
            c => updateMetadataAsync(avatarUrl, c),
            avatarStorage,
            ct);
    }

    public static async Task<Response> HandleAvatarUrlUploadAsync(
        this HttpContext http,
        string? requestUrl,
        Func<CancellationToken, Task<IAvatarBearing?>> getExistingAvatarAsync,
        Func<AvatarUrl, CancellationToken, Task<Response>> updateMetadataAsync,
        IAvatarStorage avatarStorage,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestUrl))
            return new ErrorResponse("Avatar URL payload required.", ErrorCodes.AvatarUrlInvalid, HttpStatusCode.BadRequest);

        if (!AvatarUrlValidator.TryNormalize(requestUrl, out var url, out var err))
            return new ErrorResponse("Invalid avatar URL.", err, HttpStatusCode.BadRequest);

        var normalisedUrl = new AvatarUrl(url);
        return await http.RunAvatarMetadataChangeAsync(
            getExistingAvatarAsync,
            c => updateMetadataAsync(normalisedUrl, c),
            avatarStorage,
            ct);
    }

    public static Task<Response> HandleAvatarDeleteAsync(
        this HttpContext http,
        Func<CancellationToken, Task<IAvatarBearing?>> getExistingAvatarAsync,
        Func<CancellationToken, Task<Response>> updateMetadataAsync,
        IAvatarStorage avatarStorage,
        CancellationToken ct)
        => http.RunAvatarMetadataChangeAsync(getExistingAvatarAsync, updateMetadataAsync, avatarStorage, ct);

    private static async Task<Response> RunAvatarMetadataChangeAsync(
        this HttpContext http,
        Func<CancellationToken, Task<IAvatarBearing?>> getExistingAvatarAsync,
        Func<CancellationToken, Task<Response>> updateMetadataAsync,
        IAvatarStorage avatarStorage,
        CancellationToken ct)
    {
        AvatarUrl? currentAvatarUrl = null;
        AvatarSource? currentAvatarSource = null;
        try
        {
            var existingAvatar = await getExistingAvatarAsync(ct);
            currentAvatarUrl = existingAvatar?.AvatarUrl;
            currentAvatarSource = existingAvatar?.AvatarSource;
        }
        catch { }

        var result = await updateMetadataAsync(ct);
        if (!result.IsSuccess) return result;

        if (currentAvatarSource == AvatarSource.Local && currentAvatarUrl is not null)
        {
            try { await avatarStorage.DeleteByUrlAsync(currentAvatarUrl, ct); } catch { }
        }

        return result;
    }

    public static RouteGroupBuilder MapInterfoldGroup(this IEndpointRouteBuilder endpoints, string prefix)
        => endpoints.MapGroup(prefix).AddEndpointFilter<AlterIdValidationFilter>();
}
