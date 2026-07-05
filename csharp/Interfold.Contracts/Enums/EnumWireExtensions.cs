namespace Interfold.Contracts.Enums;

/// <summary>
/// Wire-string translation for the enum surface that <see cref="LowerCaseEnumJsonConverter{TEnum}"/>
/// covers on the JSON side. Non-JSON callers (compose <c>.env</c> parameter values, Aspire
/// <c>Parameters:*</c> keys, error messages, systemd unit args) call these helpers to obtain the
/// same canonical spelling the JSON converter would emit — keeping every wire representation for
/// each enum in exactly one place.
/// </summary>
public static class EnumWireExtensions
{
    public static string ToWireValue(this DatabaseMode mode) => mode switch
    {
        DatabaseMode.Single => "single",
        DatabaseMode.Multi => "multi",
        DatabaseMode.Cassandra => "cassandra",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unhandled DatabaseMode."),
    };

    public static string ToWireValue(this NodeGroup group) => group switch
    {
        NodeGroup.Primary => "primary",
        NodeGroup.Auxiliary => "auxiliary",
        NodeGroup.Sidecar => "sidecar",
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unhandled NodeGroup."),
    };

    public static string ToWireValue(this OAuthProvider provider) => provider switch
    {
        OAuthProvider.Discord => "discord",
        OAuthProvider.Google => "google",
        OAuthProvider.Apple => "apple",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unhandled OAuthProvider."),
    };

    /// <summary>
    /// Case-insensitive parse of an OAuth provider route segment. Returns null for
    /// null / empty / unknown values so callers can keep emitting the legacy
    /// "unsupported provider" response instead of throwing.
    /// </summary>
    public static OAuthProvider? TryParseOAuthProvider(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "discord" => OAuthProvider.Discord,
            "google" => OAuthProvider.Google,
            "apple" => OAuthProvider.Apple,
            _ => null,
        };
    }

    public static string ToWireValue(this ClientPlatform platform) => platform switch
    {
        ClientPlatform.Android => "android",
        ClientPlatform.Ios => "ios",
        ClientPlatform.Web => "web",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unhandled ClientPlatform."),
    };

    /// <summary>
    /// Case-insensitive parse of a client platform value. Returns null for null / empty /
    /// unknown values so callers can keep emitting their legacy invalid-platform responses
    /// (HTTP 400 body, socket "unknown" fallback) instead of throwing.
    /// </summary>
    public static ClientPlatform? TryParseClientPlatform(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "android" => ClientPlatform.Android,
            "ios" => ClientPlatform.Ios,
            "web" => ClientPlatform.Web,
            _ => null,
        };
    }

    public static string ToWireValue(this SettingsAction action) => action switch
    {
        SettingsAction.DescriptionUpdated => "description_updated",
        SettingsAction.EncryptionReset => "encryption_reset",
        SettingsAction.PushTokenAdded => "push_token_added",
        SettingsAction.PushTokenRemoved => "push_token_removed",
        SettingsAction.AvatarUploaded => "avatar_uploaded",
        SettingsAction.AvatarDeleted => "avatar_deleted",
        SettingsAction.AccountDeleted => "account_deleted",
        SettingsAction.TagsWiped => "tags_wiped",
        SettingsAction.AltersWiped => "alters_wiped",
        SettingsAction.EmailUnlinked => "email_unlinked",
        SettingsAction.AppleUnlinked => "apple_unlinked",
        SettingsAction.DiscordUnlinked => "discord_unlinked",
        SettingsAction.FieldUpdated => "field_updated",
        SettingsAction.FieldRelocated => "field_relocated",
        SettingsAction.FieldDeleted => "field_deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unhandled SettingsAction."),
    };

    public static string ToWireValue(this Operations.ResolutionHint hint) => hint switch
    {
        Operations.ResolutionHint.NoRetry => "no_retry",
        Operations.ResolutionHint.ManualMergeRequired => "manual_merge_required",
        _ => throw new ArgumentOutOfRangeException(nameof(hint), hint, "Unhandled ResolutionHint."),
    };

    public static string ToWireValue(this ScyllaKeyspace keyspace) => keyspace switch
    {
        ScyllaKeyspace.Nam => "nam",
        ScyllaKeyspace.Eur => "eur",
        ScyllaKeyspace.Sam => "sam",
        ScyllaKeyspace.Sas => "sas",
        ScyllaKeyspace.Eas => "eas",
        ScyllaKeyspace.Ocn => "ocn",
        ScyllaKeyspace.Gdpr => "gdpr",
        _ => throw new ArgumentOutOfRangeException(nameof(keyspace), keyspace, "Unhandled ScyllaKeyspace."),
    };

    /// <summary>
    /// Parses a raw <c>OCTOCON_NODE_GROUP</c> / <c>FLY_PROCESS_GROUP</c> env value into a
    /// <see cref="NodeGroup"/>. Null / empty / whitespace resolves to <see cref="NodeGroup.Auxiliary"/>
    /// (the historical default); unknown non-empty values throw so an operator typo doesn't silently
    /// degrade a Primary to an Auxiliary.
    /// </summary>
    public static NodeGroup ParseNodeGroup(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return NodeGroup.Auxiliary;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "primary" => NodeGroup.Primary,
            "auxiliary" => NodeGroup.Auxiliary,
            "sidecar" => NodeGroup.Sidecar,
            var unknown => throw new InvalidOperationException(
                $"Unrecognised node group '{unknown}'. Valid values: primary, auxiliary, sidecar."),
        };
    }

    /// <summary>
    /// Parses a raw <c>OCTOCON_SCYLLA_KEYSPACE</c> / <c>databaseMode</c>-preview string into a
    /// <see cref="ScyllaKeyspace"/>. Null / empty resolves to <see cref="ScyllaKeyspace.Nam"/> (the
    /// deployment-time default). Unknown non-empty values throw for the same reason as
    /// <see cref="ParseNodeGroup"/>.
    /// </summary>
    public static ScyllaKeyspace ParseScyllaKeyspace(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ScyllaKeyspace.Nam;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "nam" => ScyllaKeyspace.Nam,
            "eur" => ScyllaKeyspace.Eur,
            "sam" => ScyllaKeyspace.Sam,
            "sas" => ScyllaKeyspace.Sas,
            "eas" => ScyllaKeyspace.Eas,
            "ocn" => ScyllaKeyspace.Ocn,
            "gdpr" => ScyllaKeyspace.Gdpr,
            var unknown => throw new InvalidOperationException(
                $"Unrecognised scylla keyspace '{unknown}'. Valid values: nam, eur, sam, sas, eas, ocn, gdpr."),
        };
    }

    /// <summary>
    /// Tolerant <see cref="DatabaseMode"/> parser used by <c>PrerequisitesPhase</c>'s pre-config
    /// JSON peek — returns null (rather than throwing) for null / empty / unknown values so the
    /// AIO-sizing fallback still fires when the JSON is malformed. Full-schema validation lives
    /// in <c>ConfigPhase</c>.
    /// </summary>
    public static DatabaseMode? TryParseDatabaseMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "single" => DatabaseMode.Single,
            "multi" => DatabaseMode.Multi,
            "cassandra" => DatabaseMode.Cassandra,
            _ => null,
        };
    }
}
