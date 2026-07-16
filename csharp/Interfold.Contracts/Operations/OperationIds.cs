using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Operations;

public static class OperationIds
{
    // Phase B canonical – used by the HTTP API
    public static readonly OperationId SettingsUsernameUpdate = new("cmd.settings.username.update");
    public static readonly OperationId SettingsDescriptionUpdate = new("cmd.settings.description.update");
    public static readonly OperationId SettingsPushTokenAdd = new("cmd.settings.push_token.add");
    public static readonly OperationId SettingsPushTokenRemove = new("cmd.settings.push_token.remove");
    public static readonly OperationId SettingsEncryptionRecover = new("cmd.settings.encryption.recover");
    public static readonly OperationId SettingsEncryptionReset = new("cmd.settings.encryption.reset");
    public static readonly OperationId SettingsLinkToken = new("qry.settings.link_token");
    public static readonly OperationId SettingsAvatarUpload = new("cmd.settings.avatar.upload");
    public static readonly OperationId SettingsAvatarDelete = new("cmd.settings.avatar.delete");
    public static readonly OperationId SettingsImportPk = new("cmd.settings.import.pk");
    public static readonly OperationId SettingsImportSp = new("cmd.settings.import.sp");
    public static readonly OperationId SettingsAuthUnlinkDiscord = new("cmd.settings.auth.unlink_discord");
    public static readonly OperationId SettingsAuthUnlinkEmail = new("cmd.settings.auth.unlink_email");
    public static readonly OperationId SettingsAuthUnlinkApple = new("cmd.settings.auth.unlink_apple");
    public static readonly OperationId SettingsAccountDelete = new("cmd.settings.account.delete");
    public static readonly OperationId SettingsAltersWipe = new("cmd.settings.alters.wipe");
    public static readonly OperationId SettingsTagsWipe = new("cmd.settings.tags.wipe");
    public static readonly OperationId SettingsFieldCreate = new("cmd.settings.field.create");
    public static readonly OperationId SettingsFieldUpdate = new("cmd.settings.field.update");
    public static readonly OperationId SettingsFieldDelete = new("cmd.settings.field.delete");
    public static readonly OperationId SettingsFieldRelocate = new("cmd.settings.field.relocate");

    public static readonly OperationId QueryFrontMonth = new("qry.front.month");
    public static readonly OperationId QueryFrontBetween = new("qry.front.between");
    public static readonly OperationId QueryFrontGet = new("qry.front.get");
    public static readonly OperationId QuerySystemHeartbeat = new("qry.system.heartbeat");
    public static readonly OperationId QuerySystemPublicGet = new("qry.system.public.get");
    public static readonly OperationId QueryAlterPublicList = new("qry.alter.public.list");
    public static readonly OperationId QueryAlterPublicGet = new("qry.alter.public.get");
    public static readonly OperationId QueryTagPublicList = new("qry.tag.public.list");
    public static readonly OperationId QueryTagPublicGet = new("qry.tag.public.get");
    public static readonly OperationId QueryFrontPublicList = new("qry.front.public.list");
    public static readonly OperationId QuerySystemPublicBatch = new("qry.system.public.batch");
    public static readonly OperationId QueryAuthOAuthRequest = new("qry.auth.oauth.request");
    public static readonly OperationId AuthOAuthCallback = new("cmd.auth.oauth.callback");
    public static readonly OperationId AuthRevokeToken = new("cmd.auth.revoke.token");
    public static readonly OperationId QueryAuthLinkRequest = new("qry.auth.link.request");
    public static readonly OperationId AuthLinkCallback = new("cmd.auth.link.callback");

    // Legacy CLI name retained for backward compatibility with idempotency records
    public static readonly OperationId AccountUsernameUpdate = new("cmd.account.username.update");

    public static readonly OperationId AlterCreate = new("cmd.alter.create");
    public static readonly OperationId AlterUpdate = new("cmd.alter.update");
    public static readonly OperationId AlterDelete = new("cmd.alter.delete");
    public static readonly OperationId AlterAvatarUpload = new("cmd.alter.avatar.upload");
    public static readonly OperationId AlterAvatarDelete = new("cmd.alter.avatar.delete");
    public static readonly OperationId FrontStart = new("cmd.front.start");
    public static readonly OperationId FrontEnd = new("cmd.front.end");
    public static readonly OperationId FrontBulkUpdate = new("cmd.front.bulk_update");
    public static readonly OperationId FrontSet = new("cmd.front.set");
    public static readonly OperationId FrontPrimary = new("cmd.front.primary");
    public static readonly OperationId FrontDelete = new("cmd.front.delete");
    public static readonly OperationId FrontCommentUpdate = new("cmd.front.comment.update");
    public static readonly OperationId TagCreate = new("cmd.tag.create");
    public static readonly OperationId FriendDelete = new("cmd.friend.delete");
    public static readonly OperationId FriendTrust = new("cmd.friend.trust");
    public static readonly OperationId FriendUntrust = new("cmd.friend.untrust");
    public static readonly OperationId FriendRequestSend = new("cmd.friend_request.send");
    public static readonly OperationId FriendRequestCancel = new("cmd.friend_request.cancel");
    public static readonly OperationId FriendRequestAccept = new("cmd.friend_request.accept");
    public static readonly OperationId FriendRequestReject = new("cmd.friend_request.reject");
    public static readonly OperationId TagUpdate = new("cmd.tag.update");
    public static readonly OperationId TagDelete = new("cmd.tag.delete");
    public static readonly OperationId TagAttachAlter = new("cmd.tag.attach_alter");
    public static readonly OperationId TagDetachAlter = new("cmd.tag.detach_alter");
    public static readonly OperationId TagSetParent = new("cmd.tag.set_parent");
    public static readonly OperationId TagRemoveParent = new("cmd.tag.remove_parent");
    public static readonly OperationId PollCreate = new("cmd.poll.create");
    public static readonly OperationId PollUpdate = new("cmd.poll.update");
    public static readonly OperationId PollDelete = new("cmd.poll.delete");
    public static readonly OperationId JournalGlobalCreate = new("cmd.journal.global.create");
    public static readonly OperationId JournalGlobalUpdate = new("cmd.journal.global.update");
    public static readonly OperationId JournalGlobalDelete = new("cmd.journal.global.delete");
    public static readonly OperationId JournalGlobalLock = new("cmd.journal.global.lock");
    public static readonly OperationId JournalGlobalUnlock = new("cmd.journal.global.unlock");
    public static readonly OperationId JournalGlobalPin = new("cmd.journal.global.pin");
    public static readonly OperationId JournalGlobalUnpin = new("cmd.journal.global.unpin");
    public static readonly OperationId JournalGlobalAttachAlter = new("cmd.journal.global.attach_alter");
    public static readonly OperationId JournalGlobalDetachAlter = new("cmd.journal.global.detach_alter");
    public static readonly OperationId JournalAlterCreate = new("cmd.journal.alter.create");
    public static readonly OperationId JournalAlterUpdate = new("cmd.journal.alter.update");
    public static readonly OperationId JournalAlterDelete = new("cmd.journal.alter.delete");
    public static readonly OperationId JournalAlterLock = new("cmd.journal.alter.lock");
    public static readonly OperationId JournalAlterUnlock = new("cmd.journal.alter.unlock");
    public static readonly OperationId JournalAlterPin = new("cmd.journal.alter.pin");
    public static readonly OperationId JournalAlterUnpin = new("cmd.journal.alter.unpin");
    public static readonly OperationId SettingsEncryptionSetup = new("cmd.settings.encryption.setup");
}
