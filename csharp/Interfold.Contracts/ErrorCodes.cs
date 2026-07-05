namespace Interfold.Contracts;

/// <summary>
/// Central registry of the client-visible error identifiers, mirroring the
/// <see cref="SocketEventNames"/> pattern: every <c>ErrorResponse.Code</c>,
/// <c>SocketReasonResponse.Reason</c>, and <c>InterfoldException.Code</c> literal lives here
/// so the wire vocabulary is greppable in one place. The string values are frozen —
/// the Kotlin client matches on them.
/// </summary>
public static class ErrorCodes
{
    // Generic
    public const string UnknownError = "unknown_error";
    public const string BadRequest = "bad_request";

    // Friendships / friend requests
    public const string CannotSendSelf = "cannot_send_self";
    public const string CannotCancelSelf = "cannot_cancel_self";
    public const string CannotAcceptSelf = "cannot_accept_self";
    public const string CannotRejectSelf = "cannot_reject_self";
    public const string CannotTrustSelf = "cannot_trust_self";
    public const string CannotUntrustSelf = "cannot_untrust_self";
    public const string CannotViewOwnFriendship = "cannot_view_own_friendship";
    public const string CannotDeleteOwnFriendship = "cannot_delete_own_friendship";
    public const string FriendshipNotFound = "friendship_not_found";

    // Entity lookups
    public const string SystemNotFound = "system_not_found";
    public const string AlterNotFound = "alter_not_found";
    public const string TagNotFound = "tag_not_found";
    public const string PollNotFound = "poll_not_found";
    public const string FrontNotFound = "front_not_found";
    public const string JournalEntryNotFound = "journal_entry_not_found";

    // Validation
    public const string InvalidAlterId = "invalid_alter_id";
    public const string InvalidParentTagId = "invalid_parent_tag_id";
    public const string InvalidPushToken = "invalid_push_token";
    public const string InvalidEndpoint = "invalid_endpoint";
    public const string InvalidPlatform = "invalid_platform";
    public const string InvalidAnchor = "invalid_anchor";
    public const string InvalidEndAnchor = "invalid_end_anchor";
    public const string PollInvalidTimeEnd = "poll_invalid_time_end";
    public const string AvatarFileEmpty = "avatar_file_empty";
    public const string AvatarFileRequired = "avatar_file_required";
    public const string AvatarUrlInvalid = "avatar_url_invalid";

    // Auth
    public const string InvalidOAuthProvider = "invalid_oauth_provider";
    public const string InvalidToken = "invalid_token";
    public const string MissingRedirectUri = "missing_redirect_uri";
    public const string TokenRevoked = "token_revoked";

    // WebSocket HTTP upgrade errors
    public const string WebSocketUpgradeRequired = "websocket_upgrade_required";
    public const string MissingSocketToken = "missing_socket_token";

    // Internal invariants surfaced via InterfoldException
    public const string AlterCheckServerIssue = "alter_check_server_issue";
    public const string EncryptionSaltRequired = "encryption_salt_required";

    // Socket endpoint relay
    public const string SocketEndpointPayloadInvalid = "socket_endpoint_payload_invalid";
    public const string SocketEndpointMethodPathRequired = "socket_endpoint_method_path_required";
    public const string SocketEndpointPathForbidden = "socket_endpoint_path_forbidden";
    public const string SocketEndpointProxyMisrouted = "socket_endpoint_proxy_misrouted";

    /// <summary>
    /// Reasons carried on <c>SocketReasonResponse</c> frames when a socket message is refused,
    /// including the token-authorization failure reasons for <c>phx_join</c>.
    /// </summary>
    public static class SocketReasons
    {
        public const string UnsupportedProtocolVersion = "unsupported_protocol_version";
        public const string RateLimited = "rate_limited";
        public const string NotJoined = "not_joined";
        public const string EventNotImplemented = "event_not_implemented";
        public const string MissingSocketToken = "missing_socket_token";
        public const string InvalidSocketToken = "invalid_socket_token";
        public const string InvalidSocketTokenSubject = "invalid_socket_token_subject";
        public const string UnauthorizedTopic = "unauthorized_topic";
        public const string TokenRevoked = "token_revoked";
        public const string Unauthorized = "unauthorized";
    }
}
