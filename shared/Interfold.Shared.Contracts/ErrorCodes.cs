namespace Interfold.Shared.Contracts;

/// <summary>
/// Central registry of the client-visible error identifiers as typed <see cref="ErrorCode"/>
/// members, mirroring the
/// <see cref="SocketEventNames"/> pattern: every <c>ErrorResponse.Code</c>,
/// <c>SocketReasonResponse.Reason</c>, and <c>InterfoldException.Code</c> literal lives here
/// so the wire vocabulary is greppable in one place. The string values are frozen —
/// the Kotlin client matches on them.
/// </summary>
public static class ErrorCodes
{
    // Generic
    public static readonly ErrorCode UnknownError = new("unknown_error");
    public static readonly ErrorCode BadRequest = new("bad_request");

    // Friendships / friend requests
    public static readonly ErrorCode CannotSendSelf = new("cannot_send_self");
    public static readonly ErrorCode CannotCancelSelf = new("cannot_cancel_self");
    public static readonly ErrorCode CannotAcceptSelf = new("cannot_accept_self");
    public static readonly ErrorCode CannotRejectSelf = new("cannot_reject_self");
    public static readonly ErrorCode CannotTrustSelf = new("cannot_trust_self");
    public static readonly ErrorCode CannotUntrustSelf = new("cannot_untrust_self");
    public static readonly ErrorCode CannotViewOwnFriendship = new("cannot_view_own_friendship");
    public static readonly ErrorCode CannotDeleteOwnFriendship = new("cannot_delete_own_friendship");
    public static readonly ErrorCode FriendshipNotFound = new("friendship_not_found");

    // Entity lookups
    public static readonly ErrorCode SystemNotFound = new("system_not_found");
    public static readonly ErrorCode AlterNotFound = new("alter_not_found");
    public static readonly ErrorCode TagNotFound = new("tag_not_found");
    public static readonly ErrorCode PollNotFound = new("poll_not_found");
    public static readonly ErrorCode FrontNotFound = new("front_not_found");
    public static readonly ErrorCode JournalEntryNotFound = new("journal_entry_not_found");

    // Validation
    public static readonly ErrorCode InvalidAlterId = new("invalid_alter_id");
    public static readonly ErrorCode InvalidParentTagId = new("invalid_parent_tag_id");
    public static readonly ErrorCode InvalidPushToken = new("invalid_push_token");
    public static readonly ErrorCode InvalidEndpoint = new("invalid_endpoint");
    public static readonly ErrorCode InvalidPlatform = new("invalid_platform");
    public static readonly ErrorCode InvalidAnchor = new("invalid_anchor");
    public static readonly ErrorCode InvalidEndAnchor = new("invalid_end_anchor");
    public static readonly ErrorCode PollInvalidTimeEnd = new("poll_invalid_time_end");
    public static readonly ErrorCode AvatarFileEmpty = new("avatar_file_empty");
    public static readonly ErrorCode AvatarFileRequired = new("avatar_file_required");
    public static readonly ErrorCode AvatarUrlInvalid = new("avatar_url_invalid");
    public static readonly ErrorCode AvatarUrlTooLong = new("avatar_url_too_long");

    // Auth
    public static readonly ErrorCode InvalidOAuthProvider = new("invalid_oauth_provider");
    public static readonly ErrorCode InvalidToken = new("invalid_token");
    public static readonly ErrorCode MissingRedirectUri = new("missing_redirect_uri");
    public static readonly ErrorCode TokenRevoked = new("token_revoked");

    // Firebase client-config endpoint
    public static readonly ErrorCode FirebaseConfigUnavailable = new("firebase_config_unavailable");

    // Recovery-code decryption (RecoveryCodeResolver)
    public static readonly ErrorCode RecoveryCodeNotProvided = new("recovery_code_not_provided");
    public static readonly ErrorCode RecoveryCodeNotJwe = new("recovery_code_not_jwe");
    public static readonly ErrorCode DecryptionError = new("decryption_error");

    // WebSocket HTTP upgrade errors
    public static readonly ErrorCode WebSocketUpgradeRequired = new("websocket_upgrade_required");
    public static readonly ErrorCode MissingSocketToken = new("missing_socket_token");

    // Internal invariants surfaced via InterfoldException
    public static readonly ErrorCode AlterCheckServerIssue = new("alter_check_server_issue");
    public static readonly ErrorCode EncryptionSaltRequired = new("encryption_salt_required");

    // Socket endpoint relay
    public static readonly ErrorCode SocketEndpointPayloadInvalid = new("socket_endpoint_payload_invalid");
    public static readonly ErrorCode SocketEndpointMethodPathRequired = new("socket_endpoint_method_path_required");
    public static readonly ErrorCode SocketEndpointPathForbidden = new("socket_endpoint_path_forbidden");
    public static readonly ErrorCode SocketEndpointProxyMisrouted = new("socket_endpoint_proxy_misrouted");

    /// <summary>
    /// Reasons carried on <c>SocketReasonResponse</c> frames when a socket message is refused,
    /// including the token-authorization failure reasons for <c>phx_join</c>.
    /// </summary>
    public static class SocketReasons
    {
        public static readonly ErrorCode UnsupportedProtocolVersion = new("unsupported_protocol_version");
        public static readonly ErrorCode RateLimited = new("rate_limited");
        public static readonly ErrorCode NotJoined = new("not_joined");
        public static readonly ErrorCode EventNotImplemented = new("event_not_implemented");
        public static readonly ErrorCode MissingSocketToken = ErrorCodes.MissingSocketToken;
        public static readonly ErrorCode InvalidSocketToken = new("invalid_socket_token");
        public static readonly ErrorCode InvalidSocketTokenSubject = new("invalid_socket_token_subject");
        public static readonly ErrorCode UnauthorizedTopic = new("unauthorized_topic");
        public static readonly ErrorCode TokenRevoked = new("token_revoked");
        public static readonly ErrorCode Unauthorized = new("unauthorized");
    }
}
