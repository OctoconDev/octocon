namespace Interfold.Contracts;

public static class SocketEventNames
{
    public static class Alters
    {
        //alters_created has not been added yet
        public const string Created = "alter_created";
        public const string Updated = "alter_updated";
        public const string Deleted = "alter_deleted";
    }

    public static class Tags
    {
        public const string Created = "tag_created";
        public const string Updated = "tag_updated";
        public const string Deleted = "tag_deleted";
    }

    public static class Polls
    {
        public const string Created = "poll_created";
        public const string Updated = "poll_updated";
        public const string Deleted = "poll_deleted";
    }

    public static class Imports
    {
        public const string SpComplete = "sp_import_complete";
        public const string SpFailed = "sp_import_failed";
        public const string PkComplete = "pk_import_complete";
        public const string PkFailed = "pk_import_failed";
    }

    public static class Fronting
    {
        public const string Started = "fronting_started";
        public const string Ended = "fronting_ended";
        public const string Set = "fronting_set";
        public const string BulkUpdated = "fronting_bulk";
        public const string CommentUpdated = "front_updated";
        public const string PrimaryChanged = "primary_front";
        public const string Deleted = "front_deleted";
    }

    public static class Journals
    {
        public const string GlobalCreated = "global_journal_entry_created";
        public const string GlobalUpdated = "global_journal_entry_updated";
        public const string GlobalDeleted = "global_journal_entry_deleted";
        public const string AlterCreated = "alter_journal_entry_created";
        public const string AlterUpdated = "alter_journal_entry_updated";
        public const string AlterDeleted = "alter_journal_entry_deleted";
    }

    public static class Friendships
    {
        public const string Added = "friend_added";
        public const string Removed = "friend_removed";
        public const string Trusted = "friend_trusted";
        public const string Untrusted = "friend_untrusted";
        public const string RequestSent = "friend_request_sent";
        public const string RequestReceived = "friend_request_received";
        public const string RequestRemoved = "friend_request_removed";
    }

    public static class BatchedInit {
        public const string Alters = "batched_init_alters";
        public const string Tags = "batched_init_tags";
        public const string Fronts = "batched_init_fronts";
        public const string Complete = "batched_init_complete";
    }

    public static class Settings
    {
        public const string FieldsUpdated = "fields_updated";
        public const string UsernameUpdated = "username_updated";
        public const string SelfUpdated = "self_updated";
        public const string AccountDeleted = "account_deleted";
        public const string AltersWiped = "alters_wiped";
        public const string TagsWiped = "tags_wiped";
        public const string EncryptedDataWiped = "encrypted_data_wiped";
        public const string DiscordAccountUnlinked = "discord_account_unlinked";
        public const string AppleAccountUnlinked = "apple_account_unlinked";
        public const string GoogleAccountUnlinked = "google_account_unlinked";
        public const string DiscordAccountLinked = "discord_account_linked";
        public const string GoogleAccountLinked = "google_account_linked";
        public const string AppleAccountLinked = "apple_account_linked";
    }
}