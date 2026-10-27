using System.Text.Json.Serialization;
using Interfold.Auth.Api.Helpers;
using Interfold.Auth.Api.Models;
using Interfold.Auth.Api.Services.OAuth;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;

namespace Interfold.Auth.Api.Json;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(UtcDateTimeConverter), typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(LoginMethodsResponse))]
[JsonSerializable(typeof(CloudflareSessionResponse))]
[JsonSerializable(typeof(ErrorMessageResponse))]
[JsonSerializable(typeof(UnsupportedOAuthProviderResponse))]
[JsonSerializable(typeof(OAuthTokenResponse))]
[JsonSerializable(typeof(GoogleUserInfoResponse))]
[JsonSerializable(typeof(DiscordUserResponse))]
[JsonSerializable(typeof(JwsHeader))]
[JsonSerializable(typeof(AppleIdTokenPayload))]
public sealed partial class AuthApiJsonContext : JsonSerializerContext;
