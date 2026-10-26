using System.Net;
using System.Text;
using System.Text.Json;
using Interfold.Auth.Contracts.Configuration;
using Interfold.Infrastructure.Coordination;
using Interfold.Settings.Api.Helpers;
using Interfold.Settings.Api.Json;
using Interfold.Settings.Api.Services.Export;
using Interfold.Settings.Contracts;
using Interfold.Settings.Contracts.Configuration;
using Interfold.Settings.Contracts.Ids;
using Interfold.Settings.Contracts.Models.Commands;
using Interfold.Settings.Contracts.Models.Read;
using Interfold.Settings.Contracts.Models.Wire;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Settings.Domain.Accounts;
using Interfold.Settings.Domain.Settings;
using Interfold.Shared.Api.Filters;
using Interfold.Shared.Api.Http;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Api.Services;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Contracts.Operations;
using Microsoft.Extensions.Options;

namespace Interfold.Settings.Api.DependencyInjection;

public static partial class SettingsModuleServiceCollectionExtensions
{
    public static IEndpointRouteBuilder MapSettingsModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapInterfoldGroup("/api/settings");
        group.MapGet("/export", Export);
        group.MapGet("/export/formats", GetExportFormats);
        group.MapGet("/link_token", GetLinkToken);
        group.MapPost("/username", UpdateUsername);
        group.MapPost("/description", UpdateDescription);
        group.MapPost("/push-token", AddPushToken);
        group.MapDelete("/push-token", RemovePushToken);
        group.MapPost("/setup-encryption", SetupEncryption);
        group.MapPost("/recover-encryption", RecoverEncryption);
        group.MapPost("/reset-encryption", ResetEncryption);
        group.MapPut("/avatar", UploadAvatar);
        group.MapDelete("/avatar", DeleteAvatar);
        group.MapPost("/import-pk", ImportPk);
        group.MapPost("/import-sp", ImportSp);
        group.MapPost("/import", Import);
        group.MapGet("/import/formats", GetImportFormats);
        group.MapPost("/unlink_discord", UnlinkDiscord);
        group.MapPost("/unlink_email", UnlinkEmail);
        group.MapPost("/unlink_apple", UnlinkApple);
        group.MapPost("/delete-account", DeleteAccount);
        group.MapPost("/wipe-alters", WipeAlters);
        group.MapPost("/wipe-tags", WipeTags);
        group.MapPost("/fields", CreateField);
        group.MapPatch("/fields/{id}", UpdateField);
        group.MapDelete("/fields/{id}", DeleteField);
        group.MapPost("/fields/{id}/relocate", RelocateField);
        group.MapGet("/public-key", GetPublicKey).AllowAnonymous();
        group.MapGet("/firebase-config", GetFirebaseConfig).AllowAnonymous();
        group.MapGet("/discord-bot", GetDiscordBotStatus).AllowAnonymous();

        endpoints.MapGet("/api/internal/test/fcm-sends", ListFcmSends)
            .AddEndpointFilter<RequireTestHooksAttribute>();
        return endpoints;
    }

    /// <summary>
    /// Downloads the authenticated user's export in either PluralKit v2 importable form
    /// (<c>?format=pk</c>) or the full-fidelity Octocon backup (<c>?format=octocon</c>).
    /// </summary>
    private static async Task<IResult> Export(
        HttpContext http,
        WireEnum<ExportFormat>? format,
        IExportService exportService,
        CancellationToken ct)
    {
        if (format is not { } fmt)
            return (Response)new ErrorResponse("Invalid export format.", ErrorCodes.InvalidExportFormat, HttpStatusCode.BadRequest);

        var bytes = fmt.Value switch
        {
            ExportFormat.Pk => JsonSerializer.SerializeToUtf8Bytes(
                await exportService.BuildPkAsync(http.PrincipalId(), ct),
                ExportJsonContext.Default.PkExportPayload),
            ExportFormat.Octocon => JsonSerializer.SerializeToUtf8Bytes(
                await exportService.BuildOctoconAsync(http.PrincipalId(), ct),
                ExportJsonContext.Default.OctoconExportPayload),
            _ => throw new InvalidOperationException($"Unhandled ExportFormat: {fmt.Value}"),
        };
        var wire = EnumWire<ExportFormat>.ToWire(fmt.Value);
        return TypedResults.File(bytes, "application/json", $"octocon_export_{wire}.json");
    }

    /// <summary>Returns the export formats currently supported by the API.</summary>
    private static Response<ExportFormat[]> GetExportFormats()
        => Enum.GetValues<ExportFormat>();

    private static async Task<Response<LinkTokenReadModel>> GetLinkToken(
        HttpContext http, CreateLinkTokenCommandHandler createLinkTokenHandler, CancellationToken ct)
    {
        var result = await createLinkTokenHandler.HandleAsync(http.BuildEnvelope(OperationIds.SettingsLinkToken, new CreateLinkTokenCommand()), ct);
        return new SuccessResponse<LinkTokenReadModel>(new LinkTokenReadModel(result.Result!.Token));
    }

    private static Task<Response> UpdateUsername(
        HttpContext http, SettingsUsernameRequest req, UpdateUsernameCommandHandler usernameHandler, CancellationToken ct)
        => http.DispatchNoContentAsync(usernameHandler, OperationIds.SettingsUsernameUpdate, new UpdateUsernameCommand(req.Username), ct);

    private static Task<Response> UpdateDescription(
        HttpContext http, SettingsDescriptionRequest req, UpdateDescriptionCommandHandler descriptionHandler, CancellationToken ct)
        => http.DispatchNoContentAsync(descriptionHandler, OperationIds.SettingsDescriptionUpdate, new UpdateDescriptionCommand(req.Description), ct);

    private static Task<Response> AddPushToken(
        HttpContext http, SettingsPushTokenRequest req, AddPushTokenCommandHandler addPushTokenHandler, CancellationToken ct)
    {
        if (req.Token is not { } pushToken || string.IsNullOrWhiteSpace(pushToken.Value))
            return Task.FromResult<Response>(new ErrorResponse("Invalid push token.", ErrorCodes.InvalidPushToken, HttpStatusCode.BadRequest));

        return http.DispatchNoContentAsync(addPushTokenHandler, OperationIds.SettingsPushTokenAdd, new AddPushTokenCommand(pushToken), ct);
    }

    private static Task<Response> RemovePushToken(
        HttpContext http, SettingsPushTokenRequest req, RemovePushTokenCommandHandler removePushTokenHandler, CancellationToken ct)
    {
        if (req.Token is not { } pushToken || string.IsNullOrWhiteSpace(pushToken.Value))
            return Task.FromResult<Response>(new ErrorResponse("Invalid push token.", ErrorCodes.InvalidPushToken, HttpStatusCode.BadRequest));

        return http.DispatchNoContentAsync(removePushTokenHandler, OperationIds.SettingsPushTokenRemove, new RemovePushTokenCommand(pushToken), ct);
    }

    private static Task<Response<EncryptionKeyResponse>> SetupEncryption(
        HttpContext http,
        SettingsEncryptionRequest req,
        SetupEncryptionCommandHandler setupEncryptionHandler,
        IOptionsMonitor<AuthenticationConfiguration> authenticationConfiguration,
        CancellationToken ct)
    {
        if (!TryResolveRecoveryCode(req.RecoveryCode.Value, authenticationConfiguration.CurrentValue, out var recoveryCode, out var decryptionErrorCode))
            return Task.FromResult<Response<EncryptionKeyResponse>>(new ErrorResponse("Failed to decrypt recovery code.", decryptionErrorCode, HttpStatusCode.BadRequest));

        return http.DispatchOkAsync(
            setupEncryptionHandler,
            OperationIds.SettingsEncryptionSetup,
            new SetupEncryptionCommand(recoveryCode),
            ToEncryptionKeyResponse,
            ct);
    }

    private static Task<Response<EncryptionKeyResponse>> RecoverEncryption(
        HttpContext http,
        SettingsEncryptionRequest req,
        RecoverEncryptionCommandHandler recoverEncryptionHandler,
        IOptionsMonitor<AuthenticationConfiguration> authenticationConfiguration,
        CancellationToken ct)
    {
        if (!TryResolveRecoveryCode(req.RecoveryCode.Value, authenticationConfiguration.CurrentValue, out var recoveryCode, out var decryptionErrorCode))
            return Task.FromResult<Response<EncryptionKeyResponse>>(new ErrorResponse("Failed to decrypt recovery code.", decryptionErrorCode, HttpStatusCode.BadRequest));

        return http.DispatchOkAsync(
            recoverEncryptionHandler,
            OperationIds.SettingsEncryptionRecover,
            new RecoverEncryptionCommand(recoveryCode),
            ToEncryptionKeyResponse,
            ct);
    }

    private static Task<Response> ResetEncryption(
        HttpContext http, ResetEncryptionCommandHandler resetEncryptionHandler, CancellationToken ct)
        => http.DispatchNoContentAsync(resetEncryptionHandler, OperationIds.SettingsEncryptionReset, new ResetEncryptionCommand(), ct);

    private static Task<Response> UploadAvatar(
        HttpContext http,
        IAccountRepository accountRepository,
        UploadAvatarCommandHandler uploadAvatarHandler,
        IAvatarStorage avatarStorage,
        CancellationToken ct)
    {
        if (http.Request.HasJsonContentType())
            return UploadAvatarByUrl(http, accountRepository, uploadAvatarHandler, avatarStorage, ct);

        return http.HandleAvatarUploadAsync(
            async c => await accountRepository.GetPublicProfileAsync(http.PrincipalId(), c),
            (principal, stream, c) => avatarStorage.SaveSystemAvatarAsync(principal, stream, c),
            async (url, c) => http.CommandNoContent(await uploadAvatarHandler.HandleAsync(http.BuildEnvelope(OperationIds.SettingsAvatarUpload, new UploadAvatarCommand(url, AvatarSource.Local)), c)),
            avatarStorage,
            ct);
    }

    private static async Task<Response> UploadAvatarByUrl(
        HttpContext http,
        IAccountRepository accountRepository,
        UploadAvatarCommandHandler uploadAvatarHandler,
        IAvatarStorage avatarStorage,
        CancellationToken ct)
    {
        var req = await http.Request.ReadFromJsonAsync(SharedContractsJsonContext.Default.AvatarUrlUploadRequest, ct);
        if (req is null)
            return new ErrorResponse("Avatar URL payload required.", ErrorCodes.AvatarUrlInvalid, HttpStatusCode.BadRequest);

        return await http.HandleAvatarUrlUploadAsync(
            req.Url.Value,
            async c => await accountRepository.GetPublicProfileAsync(http.PrincipalId(), c),
            async (url, c) => http.CommandNoContent(await uploadAvatarHandler.HandleAsync(http.BuildEnvelope(OperationIds.SettingsAvatarUpload, new UploadAvatarCommand(url, AvatarSource.External)), c)),
            avatarStorage,
            ct);
    }

    private static Task<Response> DeleteAvatar(
        HttpContext http,
        IAccountRepository accountRepository,
        DeleteAvatarCommandHandler deleteAvatarHandler,
        IAvatarStorage avatarStorage,
        CancellationToken ct)
        => http.HandleAvatarDeleteAsync(
            async c => await accountRepository.GetPublicProfileAsync(http.PrincipalId(), c),
            async c => http.CommandNoContent(await deleteAvatarHandler.HandleAsync(http.BuildEnvelope(OperationIds.SettingsAvatarDelete, new DeleteAvatarCommand()), c)),
            avatarStorage,
            ct);

    /// <summary>Dispatches a PluralKit import onto the background worker. Returns 202 Accepted.</summary>
    private static Task<Response<ImportDispatchResponse>> ImportPk(
        HttpContext http, SettingsImportRequest req, ImportPkCommandHandler importPkHandler, CancellationToken ct)
        => DispatchImportPkAsync(http, req, importPkHandler, ct);

    /// <summary>Dispatches a Simply Plural import onto the background worker. Returns 202 Accepted.</summary>
    private static Task<Response<ImportDispatchResponse>> ImportSp(
        HttpContext http,
        SettingsImportRequest req,
        ImportSpCommandHandler importSpHandler,
        IOptionsMonitor<AuthenticationConfiguration> authenticationConfiguration,
        CancellationToken ct)
        => DispatchImportSpAsync(http, req, importSpHandler, authenticationConfiguration, ct);

    /// <summary>Central import endpoint. Dispatches an asynchronous import for <c>octocon</c>, <c>pk</c>, or <c>sp</c>.</summary>
    private static Task<Response<ImportDispatchResponse>> Import(
        HttpContext http,
        WireEnum<ImportFormat>? format,
        JsonElement payload,
        ImportPkCommandHandler importPkHandler,
        ImportSpCommandHandler importSpHandler,
        ImportOctoconCommandHandler importOctoconHandler,
        IOptionsMonitor<AuthenticationConfiguration> authenticationConfiguration,
        CancellationToken ct)
    {
        if (format is not { } fmt)
            return Task.FromResult<Response<ImportDispatchResponse>>(new ErrorResponse("Invalid import format.", ErrorCodes.InvalidImportFormat, HttpStatusCode.BadRequest));

        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return Task.FromResult<Response<ImportDispatchResponse>>(new ErrorResponse("Import payload cannot be empty.", ErrorCodes.BadRequest, HttpStatusCode.BadRequest));

        return fmt.Value switch
        {
            ImportFormat.Octocon => DispatchImportOctoconAsync(http, payload.GetRawText(), importOctoconHandler, ct),
            ImportFormat.PluralKit => ImportRequestJson.TryDeserialize(payload, out var pkReq, out var pkError)
                ? DispatchImportPkAsync(http, pkReq, importPkHandler, ct)
                : Task.FromResult<Response<ImportDispatchResponse>>(pkError),
            ImportFormat.SimplyPlural => ImportRequestJson.TryDeserialize(payload, out var spReq, out var spError)
                ? DispatchImportSpAsync(http, spReq, importSpHandler, authenticationConfiguration, ct)
                : Task.FromResult<Response<ImportDispatchResponse>>(spError),
            _ => throw new InvalidOperationException($"Unhandled ImportFormat: {fmt.Value}"),
        };
    }

    private static async Task<Response<ImportDispatchResponse>> DispatchImportPkAsync(
        HttpContext http, SettingsImportRequest req, ImportPkCommandHandler importPkHandler, CancellationToken ct)
    {
        var envelope = http.BuildEnvelope(OperationIds.SettingsImportPk, new ImportPkCommand(req.Token));
        return http.CommandAccepted(
            await importPkHandler.HandleAsync(envelope, ct),
            r => new ImportDispatchResponse(r.OperationId, r.Status, r.StartedAt));
    }

    private static async Task<Response<ImportDispatchResponse>> DispatchImportSpAsync(
        HttpContext http,
        SettingsImportRequest req,
        ImportSpCommandHandler importSpHandler,
        IOptionsMonitor<AuthenticationConfiguration> authenticationConfiguration,
        CancellationToken ct)
    {
        RecoveryCode? recoveryCode = null;
        if (req.RecoveryCode is { } suppliedRecoveryCode
            && !string.IsNullOrWhiteSpace(suppliedRecoveryCode.Value))
        {
            if (!TryResolveRecoveryCode(suppliedRecoveryCode.Value, authenticationConfiguration.CurrentValue, out var resolved, out var decryptionErrorCode))
                return new ErrorResponse("Failed to decrypt recovery code.", decryptionErrorCode, HttpStatusCode.BadRequest);

            recoveryCode = resolved;
        }

        var envelope = http.BuildEnvelope(OperationIds.SettingsImportSp, new ImportSpCommand(req.Token, recoveryCode));
        return http.CommandAccepted(
            await importSpHandler.HandleAsync(envelope, ct),
            r => new ImportDispatchResponse(r.OperationId, r.Status, r.StartedAt));
    }

    private static async Task<Response<ImportDispatchResponse>> DispatchImportOctoconAsync(
        HttpContext http, string rawJson, ImportOctoconCommandHandler importOctoconHandler, CancellationToken ct)
    {
        var envelope = http.BuildEnvelope(OperationIds.SettingsImportOctocon, new ImportOctoconCommand(rawJson));
        return http.CommandAccepted(
            await importOctoconHandler.HandleAsync(envelope, ct),
            r => new ImportDispatchResponse(r.OperationId, r.Status, r.StartedAt));
    }

    /// <summary>Returns the import formats currently supported by the API.</summary>
    private static Response<ImportFormat[]> GetImportFormats()
        => Enum.GetValues<ImportFormat>();

    private static Task<Response> UnlinkDiscord(
        HttpContext http, UnlinkDiscordCommandHandler unlinkDiscordHandler, CancellationToken ct)
        => http.DispatchNoContentAsync(unlinkDiscordHandler, OperationIds.SettingsAuthUnlinkDiscord, new UnlinkDiscordCommand(), ct);

    private static Task<Response> UnlinkEmail(
        HttpContext http, UnlinkEmailCommandHandler unlinkEmailHandler, CancellationToken ct)
        => http.DispatchNoContentAsync(unlinkEmailHandler, OperationIds.SettingsAuthUnlinkEmail, new UnlinkEmailCommand(), ct);

    private static Task<Response> UnlinkApple(
        HttpContext http, UnlinkAppleCommandHandler unlinkAppleHandler, CancellationToken ct)
        => http.DispatchNoContentAsync(unlinkAppleHandler, OperationIds.SettingsAuthUnlinkApple, new UnlinkAppleCommand(), ct);

    private static Task<Response> DeleteAccount(
        HttpContext http, DeleteAccountCommandHandler deleteAccountHandler, CancellationToken ct)
        => http.DispatchNoContentAsync(deleteAccountHandler, OperationIds.SettingsAccountDelete, new DeleteAccountCommand(), ct);

    private static Task<Response> WipeAlters(
        HttpContext http, WipeAltersCommandHandler wipeAltersHandler, CancellationToken ct)
        => http.DispatchNoContentAsync(wipeAltersHandler, OperationIds.SettingsAltersWipe, new WipeAltersCommand(), ct);

    private static Task<Response> WipeTags(
        HttpContext http, WipeTagsCommandHandler wipeTagsHandler, CancellationToken ct)
        => http.DispatchNoContentAsync(wipeTagsHandler, OperationIds.SettingsTagsWipe, new WipeTagsCommand(), ct);

    private static async Task<Response<FieldCreatedResponse>> CreateField(
        HttpContext http, SettingsCreateFieldRequest req, CreateFieldCommandHandler createFieldHandler, CancellationToken ct)
    {
        var envelope = http.BuildEnvelope(OperationIds.SettingsFieldCreate, new CreateFieldCommand(req.Name, req.Type ?? FieldType.Text, req.SecurityLevel ?? VisibilityLevel.Private, req.Locked ?? false, InsertedAtUtc: default));
        var execution = await createFieldHandler.HandleAsync(envelope, ct);
        if (!execution.Accepted)
            return http.ConflictToError(execution.Conflict!);

        return new SuccessResponse<FieldCreatedResponse>(new FieldCreatedResponse(execution.Result!.FieldId), HttpStatusCode.Created, execution.Result.Replay);
    }

    private static Task<Response> UpdateField(
        HttpContext http, FieldId id, SettingsUpdateFieldRequest req, UpdateFieldCommandHandler updateFieldHandler, CancellationToken ct)
        => http.DispatchNoContentAsync(updateFieldHandler, OperationIds.SettingsFieldUpdate, new UpdateFieldCommand(id, req.Name, req.SecurityLevel, req.Locked), ct);

    private static Task<Response> DeleteField(
        HttpContext http, FieldId id, DeleteFieldCommandHandler deleteFieldHandler, CancellationToken ct)
        => http.DispatchNoContentAsync(deleteFieldHandler, OperationIds.SettingsFieldDelete, new DeleteFieldCommand(id), ct);

    private static Task<Response> RelocateField(
        HttpContext http, FieldId id, SettingsRelocateFieldRequest req, RelocateFieldCommandHandler relocateFieldHandler, CancellationToken ct)
        => http.DispatchNoContentAsync(relocateFieldHandler, OperationIds.SettingsFieldRelocate, new RelocateFieldCommand(id, req.Index), ct);

    private static Response<string> GetPublicKey(IOptionsMonitor<AuthenticationConfiguration> authenticationConfiguration)
        => authenticationConfiguration.CurrentValue.Rsa256PublicKey;

    /// <summary>Serves the per-platform Firebase client-init payload. Anonymous.</summary>
    private static Response<FirebaseClientConfigResponse> GetFirebaseConfig(
        HttpContext http,
        string? platform,
        IOptionsMonitor<FirebaseClientConfiguration> firebaseClientConfiguration)
    {
        var (payload, error) = FirebaseConfigResolver.Resolve(firebaseClientConfiguration.CurrentValue, platform);
        if (error is not null)
            return error;

        FirebaseConfigResolver.ApplyCacheHeaders(http.Response, payload!);
        return new SuccessResponse<FirebaseClientConfigResponse>(payload!);
    }

    /// <summary>Whether a Discord bot is currently active on this API instance.</summary>
    private static Response<DiscordBotStatusResponse> GetDiscordBotStatus()
        => new DiscordBotStatusResponse(Active: false);

    private static Response<IReadOnlyList<FcmSendAttempt>> ListFcmSends(IFcmSendRecorder recorder)
        => new SuccessResponse<IReadOnlyList<FcmSendAttempt>>(recorder.Snapshot());

    private static bool TryResolveRecoveryCode(
        string candidate,
        AuthenticationConfiguration authenticationConfiguration,
        out RecoveryCode recoveryCode,
        out ErrorCode errorCode)
        => RecoveryCodeResolver.TryResolve(candidate, authenticationConfiguration.Rsa256PrivateKey, out recoveryCode, out errorCode);

    private static EncryptionKeyResponse ToEncryptionKeyResponse(EncryptionCommandResult result)
        => new(Convert.ToBase64String(Encoding.UTF8.GetBytes(result.Key.Value)));
}
