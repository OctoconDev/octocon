namespace Interfold.Shared.Contracts;

/// <summary>
/// Data-payload keys for FCM push messages. The Kotlin app and the web service worker
/// match on these exact strings — frozen wire contract.
/// </summary>
public static class FcmPayloadKeys
{
    public const string Type = "type";
    public const string SystemId = "system_id";
    public const string AlterIds = "alter_ids";
    public const string DeepLink = "deep_link";
}

/// <summary>
/// Values carried in <see cref="FcmPayloadKeys.Type"/>. Frozen for the same reason.
/// </summary>
public static class FcmNotificationTypes
{
    public const string FrontingChanged = "fronting_changed";
}
