using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Interfold.Alters.Api.Json;
using Interfold.Alters.Contracts;
using Interfold.Auth.Api.Json;
using Interfold.Friendships.Api.Json;
using Interfold.Friendships.Contracts;
using Interfold.Fronting.Api.Json;
using Interfold.Fronting.Contracts;
using Interfold.Journals.Api.Json;
using Interfold.Journals.Contracts;
using Interfold.Ops.Api.Json;
using Interfold.Polls.Api.Json;
using Interfold.Polls.Contracts;
using Interfold.Settings.Api.Json;
using Interfold.Settings.Contracts;
using Interfold.Shared.Api.Json;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Socket.Api.Json;
using Interfold.Socket.Contracts;
using Interfold.Systems.Api.Json;
using Interfold.Systems.Contracts;
using Interfold.Tags.Api.Json;
using Interfold.Tags.Contracts;

namespace Interfold.IntegrationTests.Shared.TestServices;

/// <summary>Test-side mirror of <see cref="SuccessResponse{TValue}"/>; the production ctor's
/// [JsonIgnore] StatusCode parameter blocks STJ deserialisation.</summary>
public sealed record TestEnvelope<T>(
    T Data,
    [property: JsonPropertyName("replay")] bool? Replay = null
);

/// <summary>Test-side mirror of <see cref="ErrorResponse"/>; same STJ constraint as
/// <see cref="TestEnvelope{T}"/>.</summary>
public sealed record TestErrorResponse(
    string Error,
    ErrorCode Code,
    [property: JsonPropertyName("entity_ref")] string? EntityRef = null,
    string? Detail = null
);

/// <summary>Shared JSON plumbing. Every request/response routes through <see cref="Options"/>
/// so bytes match the API's SnakeCaseLower serializer — the idempotency-key hash depends on
/// canonical shape.</summary>
public static class TestJson
{
    /// <summary>Mirrors the API's SnakeCaseLower JsonSerializerOptions.</summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    public static JsonTypeInfo<T> TypeInfo<T>()
        => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        InterfoldJson.InsertResolver(options, TestJsonContext.Default);
        InterfoldJson.InsertResolver(options, SharedApiJsonContext.Default);
        InterfoldJson.InsertResolver(options, SharedContractsJsonContext.Default);
        InterfoldJson.InsertResolver(options, AltersContractsJsonContext.Default);
        InterfoldJson.InsertResolver(options, AltersApiJsonContext.Default);
        InterfoldJson.InsertResolver(options, TagsContractsJsonContext.Default);
        InterfoldJson.InsertResolver(options, TagsApiJsonContext.Default);
        InterfoldJson.InsertResolver(options, FriendshipsContractsJsonContext.Default);
        InterfoldJson.InsertResolver(options, FriendshipsApiJsonContext.Default);
        InterfoldJson.InsertResolver(options, JournalsContractsJsonContext.Default);
        InterfoldJson.InsertResolver(options, JournalsApiJsonContext.Default);
        InterfoldJson.InsertResolver(options, PollsContractsJsonContext.Default);
        InterfoldJson.InsertResolver(options, PollsApiJsonContext.Default);
        InterfoldJson.InsertResolver(options, FrontingContractsJsonContext.Default);
        InterfoldJson.InsertResolver(options, FrontingApiJsonContext.Default);
        InterfoldJson.InsertResolver(options, SettingsContractsJsonContext.Default);
        InterfoldJson.InsertResolver(options, SettingsApiJsonContext.Default);
        InterfoldJson.InsertResolver(options, SystemsContractsJsonContext.Default);
        InterfoldJson.InsertResolver(options, SystemsApiJsonContext.Default);
        InterfoldJson.InsertResolver(options, SocketJsonContext.Default);
        InterfoldJson.InsertResolver(options, SocketApiJsonContext.Default);
        InterfoldJson.InsertResolver(options, AuthApiJsonContext.Default);
        InterfoldJson.InsertResolver(options, OpsApiJsonContext.Default);
        return options;
    }

    /// <summary>Sends a JSON body under the specified principal's auth. Optionally sets
    /// the idempotency-key header.</summary>
    public static async Task<HttpResponseMessage> SendAsJsonAsync<TReq>(
        this HttpClient client,
        HttpMethod method,
        string path,
        TReq body,
        string principal,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: Options),
        };

        BaseEndpointTest.AttachPrincipalAuth(request, client, principal);

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            request.Headers.Add("X-Interfold-Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Authed GET (built-in HttpClient.GetAsync has no request-configure hook).</summary>
    public static async Task<HttpResponseMessage> SendAuthedGetAsync(
        this HttpClient client,
        string path,
        string principal,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        BaseEndpointTest.AttachPrincipalAuth(request, client, principal);
        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Authed DELETE.</summary>
    public static async Task<HttpResponseMessage> SendAuthedDeleteAsync(
        this HttpClient client,
        string path,
        string principal,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        BaseEndpointTest.AttachPrincipalAuth(request, client, principal);
        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Anonymous JSON send for 401-negative tests.</summary>
    public static async Task<HttpResponseMessage> SendAsJsonAnonymousAsync<TReq>(
        this HttpClient client,
        HttpMethod method,
        string path,
        TReq body,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: Options),
        };

        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Asserts the status, unwraps the API's <c>{data, replay}</c> envelope.</summary>
    public static async Task<TestEnvelope<T>> ReadEnvelopeAsync<T>(
        this HttpResponseMessage response,
        HttpStatusCode expected,
        CancellationToken ct = default)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        await Assert.That(response.StatusCode)
            .IsEqualTo(expected)
            .Because($"Expected {(int)expected} {expected} from {response.RequestMessage?.Method} " +
                     $"{response.RequestMessage?.RequestUri}, got {(int)response.StatusCode}. Body: {body}");

        var envelope = JsonSerializer.Deserialize<TestEnvelope<T>>(body, Options);
        if (envelope is null)
        {
            throw new InvalidOperationException(
                $"Failed to deserialise TestEnvelope<{typeof(T).Name}> from body: {body}");
        }

        return envelope;
    }

    /// <summary>Asserts the status and returns the parsed <see cref="TestErrorResponse"/>.</summary>
    public static async Task<TestErrorResponse> ReadErrorAsync(
        this HttpResponseMessage response,
        HttpStatusCode expected,
        CancellationToken ct = default)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        await Assert.That(response.StatusCode)
            .IsEqualTo(expected)
            .Because($"Expected {(int)expected} {expected} from {response.RequestMessage?.Method} " +
                     $"{response.RequestMessage?.RequestUri}, got {(int)response.StatusCode}. Body: {body}");

        var error = JsonSerializer.Deserialize<TestErrorResponse>(body, Options);
        if (error is null)
        {
            throw new InvalidOperationException(
                $"Failed to deserialise TestErrorResponse from body: {body}");
        }

        return error;
    }

    /// <summary>Envelope read without status assertion (caller already verified).</summary>
    public static async Task<TestEnvelope<T>> ReadEnvelopeUncheckedAsync<T>(
        this HttpResponseMessage response,
        CancellationToken ct = default)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TestEnvelope<T>>(body, Options)
               ?? throw new InvalidOperationException(
                   $"Failed to deserialise TestEnvelope<{typeof(T).Name}> from body: {body}");
    }

    /// <summary>Asserts status and deserialises the raw body (no envelope unwrap).</summary>
    public static async Task<T> ReadJsonAsync<T>(
        this HttpResponseMessage response,
        HttpStatusCode expected,
        CancellationToken ct = default)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        await Assert.That(response.StatusCode)
            .IsEqualTo(expected)
            .Because($"Expected {(int)expected} {expected} from {response.RequestMessage?.Method} " +
                     $"{response.RequestMessage?.RequestUri}, got {(int)response.StatusCode}. Body: {body}");

        var value = JsonSerializer.Deserialize<T>(body, Options);
        if (value is null)
        {
            throw new InvalidOperationException(
                $"Failed to deserialise {typeof(T).Name} from body: {body}");
        }

        return value;
    }

    /// <summary>Raw-JSON escape hatch for tests that deliberately send malformed payloads.</summary>
    public static StringContent RawJsonContent(string json)
    {
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>Authed raw-JSON send (pairs with <see cref="RawJsonContent"/>).</summary>
    public static async Task<HttpResponseMessage> SendRawJsonAsync(
        this HttpClient client,
        HttpMethod method,
        string path,
        string json,
        string principal,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = RawJsonContent(json),
        };

        BaseEndpointTest.AttachPrincipalAuth(request, client, principal);

        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }
}
