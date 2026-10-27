using System.Text.Json.Serialization;

namespace Interfold.Infrastructure.Coordination;

internal sealed class GoogleServiceAccountCredential
{
    public string? Type { get; init; }
    public string? ProjectId { get; init; }
    public string? PrivateKeyId { get; init; }
    public string? PrivateKey { get; init; }
    public string? ClientEmail { get; init; }
    public string? TokenUri { get; init; }

    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(ProjectId)
        && !string.IsNullOrWhiteSpace(ClientEmail)
        && !string.IsNullOrWhiteSpace(PrivateKey);
}

internal sealed class GoogleAccessTokenResponse
{
    public string? AccessToken { get; init; }
    public int ExpiresIn { get; init; }
    public string? TokenType { get; init; }
}

internal sealed record GoogleServiceAccountJwtHeader(string Alg, string Typ, string? Kid);

internal sealed record GoogleServiceAccountJwtPayload(
    string Iss,
    string Scope,
    string Aud,
    long Iat,
    long Exp);

internal sealed class FcmSendRequest
{
    public required FcmMessage Message { get; init; }
}

internal sealed class FcmMessage
{
    public required string Token { get; init; }
    public FcmNotification? Notification { get; init; }
    public Dictionary<string, string>? Data { get; init; }
    public FcmWebpushConfig? Webpush { get; init; }
}

internal sealed class FcmNotification
{
    public string? Title { get; init; }
    public string? Body { get; init; }
}

internal sealed class FcmWebpushConfig
{
    public FcmWebpushFcmOptions? FcmOptions { get; init; }
}

internal sealed class FcmWebpushFcmOptions
{
    public string? Link { get; init; }
}

internal sealed class GoogleErrorResponse
{
    public GoogleErrorBody? Error { get; init; }
}

internal sealed class GoogleErrorBody
{
    public int Code { get; init; }
    public string? Message { get; init; }
    public string? Status { get; init; }
    public GoogleErrorDetail[]? Details { get; init; }
}

internal sealed class GoogleErrorDetail
{
    [JsonPropertyName("@type")]
    public string? Type { get; init; }

    // FCM's error payload is camelCase here; the rest of the Google JSON is snake_case.
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }
}

internal static class FcmHttpErrorCodes
{
    public const string Unregistered = "UNREGISTERED";
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string Unavailable = "UNAVAILABLE";
    public const string Internal = "INTERNAL";
    public const string Unauthenticated = "UNAUTHENTICATED";

    public static bool IsTransient(string? code) =>
        code is Unavailable or Internal or Unauthenticated;

    public static bool IsInvalidToken(string? code) =>
        code is Unregistered or InvalidArgument;
}

internal sealed class FcmHttpException : Exception
{
    public FcmHttpException(string errorCode) : base(errorCode)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }

    public bool IsTransient => FcmHttpErrorCodes.IsTransient(ErrorCode);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GoogleServiceAccountCredential))]
[JsonSerializable(typeof(GoogleAccessTokenResponse))]
[JsonSerializable(typeof(GoogleServiceAccountJwtHeader))]
[JsonSerializable(typeof(GoogleServiceAccountJwtPayload))]
[JsonSerializable(typeof(FcmSendRequest))]
[JsonSerializable(typeof(GoogleErrorResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class FcmJsonContext : JsonSerializerContext;
