using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Operations;

/// <summary>
/// Central registry of every conflict entity-reference the Domain handlers emit,
/// mirroring the <see cref="OperationIds"/> pattern. The string values are frozen —
/// they surface verbatim as <c>ErrorResponse.EntityRef</c> on 409/422 responses and
/// integration tests assert the exact spellings.
/// </summary>
public static class EntityRefs
{
    // --- auth ---
    public static readonly EntityRef AuthLoginFailed = new("auth:login_failed");
    public static readonly EntityRef AuthLinkInvalidToken = new("auth:link_invalid_token");
    public static readonly EntityRef AuthLinkFailed = new("auth:link_failed");

    // --- account ---
    public static readonly EntityRef AccountUsernameInvalid = new("account:username_invalid");
    public static readonly EntityRef AccountUsernameTooLong = new("account:username_too_long");
    public static readonly EntityRef AccountUsernameUpdate = new("account:username_update");
    public static readonly EntityRef AccountUsernameUpdateFailed = new("account:username_update_failed");

    // --- alter ---
    public static readonly EntityRef AlterAliasTaken = new("alter:alias_taken");
    public static readonly EntityRef AlterAvatarSourceRequired = new("alter:avatar_source_required");
    public static readonly EntityRef AlterCreate = new("alter:create");
    public static readonly EntityRef AlterDelete = new("alter:delete");
    public static readonly EntityRef AlterDeleteFailed = new("alter:delete_failed");
    public static readonly EntityRef AlterId = new("alter:id");
    public static readonly EntityRef AlterName = new("alter:name");
    public static readonly EntityRef AlterNotFound = new("alter:not_found");
    public static readonly EntityRef AlterUpdate = new("alter:update");
    public static readonly EntityRef AlterUpdateNoFields = new("alter:update:no_fields");
    public static readonly EntityRef AlterUpdateFailed = new("alter:update_failed");

    // --- friend_request ---
    public static readonly EntityRef FriendRequestAccept = new("friend_request:accept");
    public static readonly EntityRef FriendRequestAlreadyFriends = new("friend_request:already_friends");
    public static readonly EntityRef FriendRequestAlreadySent = new("friend_request:already_sent");
    public static readonly EntityRef FriendRequestCancel = new("friend_request:cancel");
    public static readonly EntityRef FriendRequestNoUser = new("friend_request:no_user");
    public static readonly EntityRef FriendRequestNotRequested = new("friend_request:not_requested");
    public static readonly EntityRef FriendRequestReject = new("friend_request:reject");
    public static readonly EntityRef FriendRequestSend = new("friend_request:send");

    // --- friendship ---
    public static readonly EntityRef FriendshipNotFound = new("friendship:not_found");
    public static readonly EntityRef FriendshipRemove = new("friendship:remove");
    public static readonly EntityRef FriendshipTrust = new("friendship:trust");

    // --- fronting ---
    public static readonly EntityRef FrontingAlreadyFronting = new("fronting:already_fronting");
    public static readonly EntityRef FrontingBulkUpdate = new("fronting:bulk_update");
    public static readonly EntityRef FrontingDelete = new("fronting:delete");
    public static readonly EntityRef FrontingDeleteFailed = new("fronting:delete_failed");
    public static readonly EntityRef FrontingEnd = new("fronting:end");
    public static readonly EntityRef FrontingEndFailed = new("fronting:end_failed");
    public static readonly EntityRef FrontingInvalidAlterId = new("fronting:invalid_alter_id");
    public static readonly EntityRef FrontingInvalidComment = new("fronting:invalid_comment");
    public static readonly EntityRef FrontingInvalidFrontId = new("fronting:invalid_front_id");
    public static readonly EntityRef FrontingNoFront = new("fronting:no_front");
    public static readonly EntityRef FrontingNotFronting = new("fronting:not_fronting");
    public static readonly EntityRef FrontingPrimary = new("fronting:primary");
    public static readonly EntityRef FrontingPrimaryFailed = new("fronting:primary_failed");
    public static readonly EntityRef FrontingSet = new("fronting:set");
    public static readonly EntityRef FrontingStart = new("fronting:start");
    public static readonly EntityRef FrontingStartFailed = new("fronting:start_failed");
    public static readonly EntityRef FrontingUpdateComment = new("fronting:update_comment");
    public static readonly EntityRef FrontingUpdateCommentFailed = new("fronting:update_comment_failed");

    // --- journal ---
    public static readonly EntityRef JournalAlterCreate = new("journal:alter:create");
    public static readonly EntityRef JournalAlterDelete = new("journal:alter:delete");
    public static readonly EntityRef JournalAlterSetLocked = new("journal:alter:set_locked");
    public static readonly EntityRef JournalAlterSetPinned = new("journal:alter:set_pinned");
    public static readonly EntityRef JournalAlterUpdate = new("journal:alter:update");
    public static readonly EntityRef JournalAlterNotFound = new("journal:alter_not_found");
    public static readonly EntityRef JournalAttachFailed = new("journal:attach_failed");
    public static readonly EntityRef JournalContentTooLong = new("journal:content_too_long");
    public static readonly EntityRef JournalCreateFailed = new("journal:create_failed");
    public static readonly EntityRef JournalDeleteFailed = new("journal:delete_failed");
    public static readonly EntityRef JournalDetachFailed = new("journal:detach_failed");
    public static readonly EntityRef JournalGlobalAttachAlter = new("journal:global:attach_alter");
    public static readonly EntityRef JournalGlobalCreate = new("journal:global:create");
    public static readonly EntityRef JournalGlobalDelete = new("journal:global:delete");
    public static readonly EntityRef JournalGlobalDetachAlter = new("journal:global:detach_alter");
    public static readonly EntityRef JournalGlobalSetLocked = new("journal:global:set_locked");
    public static readonly EntityRef JournalGlobalSetPinned = new("journal:global:set_pinned");
    public static readonly EntityRef JournalGlobalUpdate = new("journal:global:update");
    public static readonly EntityRef JournalNoFields = new("journal:no_fields");
    public static readonly EntityRef JournalNotFound = new("journal:not_found");
    public static readonly EntityRef JournalTitleRequired = new("journal:title_required");
    public static readonly EntityRef JournalTitleTooLong = new("journal:title_too_long");
    public static readonly EntityRef JournalUpdateFailed = new("journal:update_failed");

    // --- poll ---
    public static readonly EntityRef PollCreate = new("poll:create");
    public static readonly EntityRef PollCreateFailed = new("poll:create_failed");
    public static readonly EntityRef PollDelete = new("poll:delete");
    public static readonly EntityRef PollDeleteFailed = new("poll:delete_failed");
    public static readonly EntityRef PollDescriptionTooLong = new("poll:description_too_long");
    public static readonly EntityRef PollNoFields = new("poll:no_fields");
    public static readonly EntityRef PollNotFound = new("poll:not_found");
    public static readonly EntityRef PollTitleRequired = new("poll:title_required");
    public static readonly EntityRef PollTitleTooLong = new("poll:title_too_long");
    public static readonly EntityRef PollUpdate = new("poll:update");
    public static readonly EntityRef PollUpdateFailed = new("poll:update_failed");

    // --- settings ---
    public static readonly EntityRef SettingsAccountDelete = new("settings:account:delete");
    public static readonly EntityRef SettingsAltersWipe = new("settings:alters:wipe");
    public static readonly EntityRef SettingsAvatarDelete = new("settings:avatar:delete");
    public static readonly EntityRef SettingsAvatarUpload = new("settings:avatar:upload");
    public static readonly EntityRef SettingsAvatarInvalid = new("settings:avatar_invalid");
    public static readonly EntityRef SettingsDescriptionUpdate = new("settings:description:update");
    public static readonly EntityRef SettingsDescriptionInvalid = new("settings:description_invalid");
    public static readonly EntityRef SettingsDescriptionUpdateFailed = new("settings:description_update_failed");
    public static readonly EntityRef SettingsEncryptionRecover = new("settings:encryption:recover");
    public static readonly EntityRef SettingsEncryptionReset = new("settings:encryption:reset");
    public static readonly EntityRef SettingsEncryptionSetup = new("settings:encryption:setup");
    public static readonly EntityRef SettingsEncryptionNotInitialized = new("settings:encryption_not_initialized");
    public static readonly EntityRef SettingsEncryptionResetFailed = new("settings:encryption_reset_failed");
    public static readonly EntityRef SettingsEncryptionSetupFailed = new("settings:encryption_setup_failed");
    public static readonly EntityRef SettingsFieldCreate = new("settings:field:create");
    public static readonly EntityRef SettingsFieldCreateFailed = new("settings:field:create_failed");
    public static readonly EntityRef SettingsFieldDelete = new("settings:field:delete");
    public static readonly EntityRef SettingsFieldRelocate = new("settings:field:relocate");
    public static readonly EntityRef SettingsFieldUpdate = new("settings:field:update");
    public static readonly EntityRef SettingsFieldNameRequired = new("settings:field_name_required");
    public static readonly EntityRef SettingsImportPkInvalid = new("settings:import_pk_invalid");
    public static readonly EntityRef SettingsImportSpInvalid = new("settings:import_sp_invalid");
    public static readonly EntityRef SettingsInvalidRecoveryCode = new("settings:invalid_recovery_code");
    public static readonly EntityRef SettingsPushTokenAdd = new("settings:push_token:add");
    public static readonly EntityRef SettingsPushTokenRemove = new("settings:push_token:remove");
    public static readonly EntityRef SettingsPushTokenAddFailed = new("settings:push_token_add_failed");
    public static readonly EntityRef SettingsPushTokenInvalid = new("settings:push_token_invalid");
    public static readonly EntityRef SettingsRecoveryCodeInvalid = new("settings:recovery_code_invalid");
    public static readonly EntityRef SettingsTagsWipe = new("settings:tags:wipe");
    public static readonly EntityRef SettingsUnlinkApple = new("settings:unlink:apple");
    public static readonly EntityRef SettingsUnlinkDiscord = new("settings:unlink:discord");
    public static readonly EntityRef SettingsUnlinkEmail = new("settings:unlink:email");

    // --- tag ---
    public static readonly EntityRef TagAlterNotFound = new("tag:alter_not_found");
    public static readonly EntityRef TagAttachAlter = new("tag:attach_alter");
    public static readonly EntityRef TagCreate = new("tag:create");
    public static readonly EntityRef TagCreateFailed = new("tag:create_failed");
    public static readonly EntityRef TagCycle = new("tag:cycle");
    public static readonly EntityRef TagDelete = new("tag:delete");
    public static readonly EntityRef TagDetachAlter = new("tag:detach_alter");
    public static readonly EntityRef TagNameRequired = new("tag:name_required");
    public static readonly EntityRef TagNameTooLong = new("tag:name_too_long");
    public static readonly EntityRef TagNoFields = new("tag:no_fields");
    public static readonly EntityRef TagNotFound = new("tag:not_found");
    public static readonly EntityRef TagParentNotFound = new("tag:parent_not_found");
    public static readonly EntityRef TagRemoveParent = new("tag:remove_parent");
    public static readonly EntityRef TagSetParent = new("tag:set_parent");
    public static readonly EntityRef TagUpdate = new("tag:update");

    /// <summary>
    /// The invariant-violation ref minted by the settings-command flow when applying a
    /// settings command fails, e.g. <c>"settings:avatar_uploaded_failed"</c> — one value per
    /// <see cref="SettingsAction"/> routed through <c>SettingsIdempotentCommandFlow</c>.
    /// </summary>
    public static EntityRef SettingsActionFailed(SettingsAction action)
        => new($"settings:{action.ToWire()}_failed");
}
