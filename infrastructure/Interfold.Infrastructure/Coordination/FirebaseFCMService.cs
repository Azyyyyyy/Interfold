using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Settings.Contracts.Configuration;
using Interfold.Settings.Contracts.Ids;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Secrets;
using Interfold.Shared.Domain.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace Interfold.Infrastructure.Coordination;

/// <summary>Real FCM v1 sender for the fronting-changed push flow. DI wires this only
/// when the primary role is active and <c>fcm:service_account_json</c> is present;
/// otherwise <see cref="NullFCMService"/> takes over.</summary>
public sealed class FirebaseFCMService : IFCMService, IDisposable
{
    // FCM v1 multicast cap: https://firebase.google.com/docs/reference/fcm/rest/v1/projects.messages/send
    private const int FcmMulticastMax = 500;

    private const string FcmSendUrlFormat = "https://fcm.googleapis.com/v1/projects/{0}/messages:send";

    private readonly IOptions<FcmConfiguration> _options;
    private readonly INotificationTokenRepository _tokens;
    private readonly IAccountRepository _accounts;
    private readonly IAlterRepository _alters;
    private readonly IFcmSendRecorder _recorder;
    private readonly ILogger<FirebaseFCMService> _logger;
    private readonly ResiliencePipeline _resilience;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly HttpClient _http;
    private readonly GoogleServiceAccountOAuth _oauth;
    private GoogleServiceAccountCredential? _credential;

    public FirebaseFCMService(
        IOptions<FcmConfiguration> options,
        INotificationTokenRepository tokens,
        IAccountRepository accounts,
        IAlterRepository alters,
        IFcmSendRecorder recorder,
        IHttpClientFactory httpClientFactory,
        ILogger<FirebaseFCMService> logger)
    {
        _options = options;
        _tokens = tokens;
        _accounts = accounts;
        _alters = alters;
        _recorder = recorder;
        _logger = logger;
        _http = httpClientFactory.CreateClient(HttpClientNames.Fcm);
        _oauth = new GoogleServiceAccountOAuth(_http);
        _resilience = BuildResiliencePipeline(logger);
    }

    public async Task NotifyFrontingChangedAsync(
        SystemId systemId,
        IReadOnlyList<AlterId> currentAlterIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(systemId.Value))
            return;

        var friendGroups = await _tokens.ListTokensForFriendsOfAsync(systemId, cancellationToken).ConfigureAwait(false);
        if (friendGroups.Count == 0)
        {
            _logger.LogInformation("[fcm] system={SystemId} has no friend tokens to notify.", systemId);
            _recorder.Record(Attempt(systemId, skip: FcmSendSkipReasons.NoFriendTokens, alterIds: currentAlterIds));
            return;
        }

        var credential = await EnsureCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            _logger.LogInformation("[fcm] system={SystemId} skipped: Firebase Messaging unavailable.", systemId);
            _recorder.Record(Attempt(systemId, skip: FcmSendSkipReasons.MessagingUnavailable, alterIds: currentAlterIds));
            return;
        }

        var profile = await _accounts.GetPublicProfileAsync(systemId, cancellationToken).ConfigureAwait(false);
        var frontingDisplayName = profile?.Username is { } username && !string.IsNullOrWhiteSpace(username.Value)
            ? username.Value
            : "A friend";

        // FCM webpush Link must be an https URL. A relative path is dropped (not sent)
        // so the multicast is not rejected for web tokens.
        var deepLink = $"/system/{Uri.EscapeDataString(systemId.Value)}";
        var frontEnded = currentAlterIds.Count == 0;
        var currentAlterSet = frontEnded ? null : new HashSet<AlterId>(currentAlterIds);

        foreach (var friendGroup in friendGroups)
        {
            IReadOnlyList<AlterId> visibleIds;
            string body;
            if (frontEnded)
            {
                visibleIds = Array.Empty<AlterId>();
                body = "No one is fronting";
            }
            else
            {
                var guarded = await _alters
                    .ListGuardedAsync(systemId, friendGroup.FriendSystemId, cancellationToken)
                    .ConfigureAwait(false);
                var visibleAlters = guarded
                    .Where(a => currentAlterSet!.Contains(a.Id))
                    .ToArray();

                if (visibleAlters.Length == 0)
                {
                    // Skip rather than leak "something changed" via an empty payload.
                    _logger.LogInformation(
                        "[fcm] Skipping friend={FriendSystemId} for system={SystemId}: no visible fronting alters.",
                        friendGroup.FriendSystemId, systemId);
                    _recorder.Record(Attempt(
                        systemId,
                        friendGroup.FriendSystemId,
                        skip: FcmSendSkipReasons.NoVisibleFronts,
                        alterIds: currentAlterIds,
                        tokenCount: friendGroup.Tokens.Count));
                    continue;
                }

                visibleIds = visibleAlters.Select(a => a.Id).ToArray();
                body = string.Join(", ", visibleAlters.Select(a => a.Name));
            }

            var alterCsv = string.Join(",", visibleIds);
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [FcmPayloadKeys.Type] = FcmNotificationTypes.FrontingChanged,
                [FcmPayloadKeys.SystemId] = systemId.Value,
                [FcmPayloadKeys.AlterIds] = alterCsv,
                [FcmPayloadKeys.DeepLink] = deepLink,
            };

            var title = $"Front update: {frontingDisplayName}";
            var notification = new FcmNotification { Title = title, Body = body };
            var webpush = TryWebpush(deepLink);
            var sendUrl = string.Format(
                FcmSendUrlFormat,
                Uri.EscapeDataString(credential.ProjectId!));

            foreach (var chunk in friendGroup.Tokens.Chunk(FcmMulticastMax))
            {
                IReadOnlyList<TokenSendResult> results;
                try
                {
                    await _resilience.ExecuteAsync(
                        async ct => await _oauth.GetAccessTokenAsync(credential, ct).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);

                    results = await Task.WhenAll(chunk.Select(token =>
                        SendOneAsync(sendUrl, token, notification, data, webpush, cancellationToken))).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[fcm] Multicast send failed for system={SystemId} friend={FriendSystemId} tokens={TokenCount}",
                        systemId, friendGroup.FriendSystemId, chunk.Length);
                    _recorder.Record(Attempt(
                        systemId,
                        friendGroup.FriendSystemId,
                        skip: FcmSendSkipReasons.MulticastFailed,
                        title: title,
                        body: body,
                        alterIds: visibleIds,
                        tokenCount: chunk.Length,
                        errorCodes: [ex.GetType().Name]));
                    continue;
                }

                var successCount = 0;
                var failureCount = 0;
                List<string>? errorCodes = null;
                foreach (var result in results)
                {
                    if (result.Success)
                    {
                        successCount++;
                        continue;
                    }

                    failureCount++;
                    (errorCodes ??= []).Add(result.ErrorCode ?? "unknown");
                }

                _recorder.Record(Attempt(
                    systemId,
                    friendGroup.FriendSystemId,
                    title: title,
                    body: body,
                    alterIds: visibleIds,
                    tokenCount: chunk.Length,
                    successCount: successCount,
                    failureCount: failureCount,
                    errorCodes: errorCodes));

                await PruneInvalidTokensAsync(chunk, results, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<TokenSendResult> SendOneAsync(
        string sendUrl,
        PushToken token,
        FcmNotification notification,
        Dictionary<string, string> data,
        FcmWebpushConfig? webpush,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _resilience.ExecuteAsync(
                async ct =>
                {
                    var accessToken = await _oauth.GetAccessTokenAsync(_credential!, ct).ConfigureAwait(false);
                    var request = new FcmSendRequest
                    {
                        Message = new FcmMessage
                        {
                            Token = token.Value,
                            Notification = notification,
                            Data = data,
                            Webpush = webpush,
                        },
                    };

                    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, sendUrl);
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    httpRequest.Content = JsonContent.Create(request, FcmJsonContext.Default.FcmSendRequest);

                    using var response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                        return new TokenSendResult(true, null, false);

                    var error = await ReadFcmErrorAsync(response, ct).ConfigureAwait(false);
                    if (error.Unauthorized)
                    {
                        _oauth.Invalidate();
                        throw new FcmHttpException(FcmHttpErrorCodes.Unauthenticated);
                    }

                    if (error.Transient)
                        throw new FcmHttpException(error.Code);

                    return new TokenSendResult(false, error.Code, error.Prune);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FcmHttpException ex)
        {
            return new TokenSendResult(false, ex.ErrorCode, false);
        }
        catch (Exception ex)
        {
            return new TokenSendResult(false, ex.GetType().Name, false);
        }
    }

    // Prune tokens flagged as permanently invalid (UNREGISTERED / INVALID_ARGUMENT); other
    // failures are logged and left in place so the next flush retries.
    private async Task PruneInvalidTokensAsync(
        IReadOnlyList<PushToken> tokens,
        IReadOnlyList<TokenSendResult> results,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (result.Success) continue;

            var token = tokens[i];
            if (result.Prune)
            {
                try
                {
                    await _tokens.RemoveAsync(token, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "[fcm] Pruned invalid token ({Code}). token_prefix={TokenPrefix}",
                        result.ErrorCode, TokenPrefix(token.Value));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "[fcm] Failed to prune invalid token. token_prefix={TokenPrefix}",
                        TokenPrefix(token.Value));
                }
            }
            else
            {
                _logger.LogWarning(
                    "[fcm] Per-token delivery failure. token_prefix={TokenPrefix} code={Code}",
                    TokenPrefix(token.Value),
                    result.ErrorCode);
            }
        }
    }

    private static string TokenPrefix(string token) =>
        string.IsNullOrEmpty(token) ? "***"
            : token.Length <= 6 ? "***"
            : token[..6];

    private async Task<GoogleServiceAccountCredential?> EnsureCredentialAsync(CancellationToken ct)
    {
        var existing = Volatile.Read(ref _credential);
        if (existing is not null) return existing;

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_credential is not null) return _credential;

            var serviceAccountJson = _options.Value.ServiceAccountJson;
            if (string.IsNullOrWhiteSpace(serviceAccountJson))
            {
                // Defensive: the snapshot is startup-populated and shouldn't change mid-process.
                _logger.LogError(
                    "[fcm] {Key} disappeared between DI-graph build and first send. " +
                    "Restart the API after re-seeding the row, or drop it and let the factory pick NullFCMService.",
                    SecretsStoreKeys.FcmServiceAccountJson);
                return null;
            }

            GoogleServiceAccountCredential? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(
                    serviceAccountJson,
                    FcmJsonContext.Default.GoogleServiceAccountCredential);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "[fcm] {Key} is not valid service-account JSON.", SecretsStoreKeys.FcmServiceAccountJson);
                return null;
            }

            if (parsed is not { IsUsable: true })
            {
                _logger.LogError(
                    "[fcm] {Key} is missing project_id, client_email, or private_key.",
                    SecretsStoreKeys.FcmServiceAccountJson);
                return null;
            }

            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(GoogleServiceAccountOAuth.NormalizePem(parsed.PrivateKey!).AsSpan());
            }
            catch (CryptographicException ex)
            {
                _logger.LogError(ex, "[fcm] {Key} private_key is not a usable RSA PEM.", SecretsStoreKeys.FcmServiceAccountJson);
                return null;
            }

            _credential = parsed;
            _logger.LogInformation("[fcm] FCM HTTP v1 sender ready (project={ProjectId}).", parsed.ProjectId);
            return _credential;
        }
        finally
        {
            _initGate.Release();
        }
    }

    // Retries the two transient FCM codes + 401-after-invalidate + transport failures.
    // Per-token so one bad device does not replay the whole 500-token chunk.
    private static ResiliencePipeline BuildResiliencePipeline(ILogger logger) =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<FcmHttpException>(ex => ex.IsTransient)
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(500),
                MaxDelay = TimeSpan.FromSeconds(5),
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception,
                        "[fcm] Transient send failure, retrying attempt {Attempt} in {Delay}ms.",
                        args.AttemptNumber + 1, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();

    public void Dispose()
    {
        _initGate.Dispose();
        _oauth.Dispose();
    }

    private static FcmWebpushConfig? TryWebpush(string deepLink)
    {
        if (Uri.TryCreate(deepLink, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new FcmWebpushConfig { FcmOptions = new FcmWebpushFcmOptions { Link = deepLink } };
        }

        return null;
    }

    private static async Task<FcmErrorRead> ReadFcmErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return new FcmErrorRead(FcmHttpErrorCodes.Unauthenticated, Transient: true, Prune: false, Unauthorized: true);

        var fallback = FallbackCode(response.StatusCode);
        try
        {
            var parsed = await response.Content
                .ReadFromJsonAsync(FcmJsonContext.Default.GoogleErrorResponse, cancellationToken)
                .ConfigureAwait(false);
            var code = parsed?.Error?.Details?.Select(d => d.ErrorCode).FirstOrDefault(static c => !string.IsNullOrEmpty(c))
                       ?? parsed?.Error?.Status
                       ?? fallback;
            return new FcmErrorRead(
                code,
                FcmHttpErrorCodes.IsTransient(code),
                FcmHttpErrorCodes.IsInvalidToken(code),
                code == FcmHttpErrorCodes.Unauthenticated);
        }
        catch (JsonException)
        {
            return new FcmErrorRead(
                fallback,
                FcmHttpErrorCodes.IsTransient(fallback),
                Prune: false,
                Unauthorized: false);
        }
    }

    private static string FallbackCode(HttpStatusCode status) => status switch
    {
        HttpStatusCode.InternalServerError => FcmHttpErrorCodes.Internal,
        HttpStatusCode.ServiceUnavailable => FcmHttpErrorCodes.Unavailable,
        _ => status.ToString(),
    };

    private static FcmSendAttempt Attempt(
        SystemId systemId,
        SystemId? friendSystemId = null,
        string? skip = null,
        string? title = null,
        string? body = null,
        IReadOnlyList<AlterId>? alterIds = null,
        int tokenCount = 0,
        int successCount = 0,
        int failureCount = 0,
        IReadOnlyList<string>? errorCodes = null)
        => new(
            SystemId: systemId.Value,
            FriendSystemId: friendSystemId?.Value,
            SkipReason: skip,
            Title: title,
            Body: body,
            AlterIds: alterIds?.Select(id => id.Value.ToString()).ToArray() ?? [],
            TokenCount: tokenCount,
            SuccessCount: successCount,
            FailureCount: failureCount,
            ErrorCodes: errorCodes ?? [],
            At: DateTimeOffset.UtcNow);

    private readonly record struct TokenSendResult(bool Success, string? ErrorCode, bool Prune);

    private readonly record struct FcmErrorRead(string Code, bool Transient, bool Prune, bool Unauthorized);
}
