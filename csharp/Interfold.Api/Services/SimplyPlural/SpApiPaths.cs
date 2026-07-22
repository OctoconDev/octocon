namespace Interfold.Api.Services.SimplyPlural;

/// <summary>Simply Plural v1 API paths + CDN hosts + wire sentinels the importer matches
/// on. All third-party wire contracts (frozen by SP).</summary>
internal static class SpApiPaths
{
    public const string ApiBase = "https://api.apparyllis.com/v1";

    public static string Me() => $"{ApiBase}/me";
    public static string CustomFields(string spSystemId) => $"{ApiBase}/customFields/{spSystemId}";
    public static string Members(string spSystemId) => $"{ApiBase}/members/{spSystemId}";
    public static string CustomFronts(string spSystemId) => $"{ApiBase}/customFronts/{spSystemId}";
    public static string Groups(string spSystemId) => $"{ApiBase}/groups/{spSystemId}";
    public static string FrontHistory(string spSystemId, long startTimeMs, long endTimeMs)
        => $"{ApiBase}/frontHistory/{spSystemId}?startTime={startTimeMs}&endTime={endTimeMs}";
    public static string CurrentFronters() => $"{ApiBase}/fronters/";
    public static string Polls(string spSystemId) => $"{ApiBase}/polls/{spSystemId}";
    public static string Notes(string spSystemId, string spMemberId) => $"{ApiBase}/notes/{spSystemId}/{spMemberId}";

    /// <summary>SP's Mongo convention for "no parent" on tag/group hierarchies.</summary>
    public const string RootGroupParent = "root";
}

/// <summary>SP-owned hosts fronting the same <c>avatars/{uid}/{uuid}</c> bucket. All three
/// CNAME the same DigitalOcean Spaces bucket — new entries must be SP-owned too.</summary>
internal static class SpCdnHosts
{
    public const string Canonical = "https://spaces.apparyllis.com";

    public static readonly string[] All =
    {
        Canonical,
        "https://serve.apparyllis.com",
        "https://simply-plural.sfo3.digitaloceanspaces.com",
    };

    public static string Avatar(string uid, string avatarUuid) => $"{Canonical}/avatars/{uid}/{avatarUuid}";
}
