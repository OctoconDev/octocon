namespace Interfold.Api.Services.SimplyPlural;

/// <summary>
/// Simply Plural's v1 API surface — base URL, endpoint path builders, CDN hosts, and the
/// wire sentinels the importer matches on. All values are third-party wire contracts
/// (frozen by SP, not by us); centralized here so the importer body carries no inline
/// endpoint spellings.
/// </summary>
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

/// <summary>
/// Every public host SP currently serves the same <c>avatars/{uid}/{uuid}</c> bucket over.
/// Checked by the importer so a pre-existing avatar URL on any of them is recognised as
/// "needs rehost before sunset" rather than being misclassified as a third-party URL.
/// Sources (SimplyPluralApi):
///   * spaces.apparyllis.com — src/api/base/user/generateReports.ts:24 (SP's own report generator)
///   * serve.apparyllis.com — src/api/v1/storage.ts:63, src/api/v2/storage/storage.utils.ts:46
///     (canonical URL the upload endpoints return to clients post-v1.12)
///   * simply-plural.sfo3.digitaloceanspaces.com — src/api/base/user.ts:11 (legacy v1 report base)
/// All three CNAME the same DigitalOcean Spaces bucket; new entries here must be SP-owned too.
/// </summary>
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
