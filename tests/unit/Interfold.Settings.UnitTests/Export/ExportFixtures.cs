using System.Text.Json;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;

namespace Interfold.Api.UnitTests.Export;

// Hand-authored fixture inputs and their pinned expected JSON, mirroring
// `Octocon.Accounts.format_pk_export` / `format_full_export` from
// octocon/lib/octocon/accounts.ex:1273-1415 against the same inputs.
// Kept close to the reference so a diff review only has to compare two JSON blobs.
internal static class ExportFixtures
{
    public static readonly SystemId OwnerId = new("owner01");

    public static readonly string PkDescription1200 =
        string.Concat(System.Linq.Enumerable.Repeat("abcdefghij", 120));

    public static readonly string DescriptionTruncatedTo1000 = PkDescription1200[..1000];

    public static readonly AvatarUrl AlterOneAvatar = new("https://cdn.example/1.png");
    public static readonly HexColor AlterOneColor = new("#abcdef");
    public static readonly AlterId AlterOneId = new(1);
    public static readonly AlterId AlterTwoId = new(2);
    public static readonly FieldId FieldOneId = new(System.Guid.Parse("11111111-1111-1111-1111-111111111111"));
    public static readonly TagId TagRootId = new(System.Guid.Parse("22222222-2222-2222-2222-222222222222"));
    public static readonly TagId TagChildId = new(System.Guid.Parse("33333333-3333-3333-3333-333333333333"));
    public static readonly PollId PollOneId = new(System.Guid.Parse("44444444-4444-4444-4444-444444444444"));
    public static readonly FrontId FrontOpenId = new(System.Guid.Parse("55555555-5555-5555-5555-555555555555"));
    public static readonly FrontId FrontClosedId = new(System.Guid.Parse("66666666-6666-6666-6666-666666666666"));

    public static readonly DateTime FixedInsertedAt = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    public static readonly DateTime FixedUpdatedAt = new(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc);
    public static readonly DateTimeOffset FrontOpenStart = new(2024, 5, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset FrontClosedStart = new(2024, 4, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset FrontClosedEnd = new(2024, 4, 2, 0, 0, 0, TimeSpan.Zero);

    public static AccountPublicProfileReadModel BuildProfile(string? description = null) =>
        new(
            OwnerId,
            new Username("ownername"),
            description ?? PkDescription1200,
            new AvatarUrl("https://cdn.example/owner.png"),
            AvatarSource.External,
            DiscordId: null,
            Email: null,
            AppleId: null);

    // pk assertion is compared via JsonNode.DeepEquals so ordering / whitespace can't
    // trip us — but the *keys* and *values* still have to be identical. The literal
    // below mirrors format_pk_export against the fixture above.
    public static string ExpectedPkJson()
    {
        var desc1000 = DescriptionTruncatedTo1000;
        return $$"""
        {
          "version": 2,
          "name": "ownername",
          "description": "{{desc1000}}",
          "avatar_url": "https://cdn.example/owner.png",
          "switches": [],
          "members": [
            {
              "id": "1",
              "name": "Alpha",
              "pronouns": "they/them",
              "description": "{{desc1000}}",
              "color": "abcdef",
              "avatar_url": "https://cdn.example/1.png",
              "proxy_tags": [{ "prefix": "prefix;", "suffix": " suffix" }],
              "display_name": "AlphaProxy"
            },
            {
              "id": "2",
              "name": "Beta",
              "pronouns": "",
              "description": "",
              "color": null,
              "avatar_url": null,
              "proxy_tags": [],
              "display_name": null
            }
          ],
          "groups": [
            {
              "id": "0",
              "name": "RootTag",
              "description": "root description",
              "color": "112233",
              "members": ["1", "2"]
            },
            {
              "id": "1",
              "name": "ChildTag",
              "description": null,
              "color": null,
              "members": ["1"]
            }
          ]
        }
        """;
    }

    public static string ExpectedFullJson()
    {
        // Full export echoes raw values without pk-side truncation. `time_start` /
        // `time_end` land as ISO-8601 with the DateTimeOffset's `+00:00`; DateTime(Kind=Utc)
        // stamps as `...Z` — STJ's built-in DateTime/DateTimeOffset writers do this
        // unconditionally, no custom converter needed.
        return """
        {
          "user": {
            "username": "ownername",
            "description": "%DESC%",
            "id": "owner01",
            "avatar_url": "https://cdn.example/owner.png",
            "fields": [
              { "id": "11111111-1111-1111-1111-111111111111", "name": "Field1", "type": "text", "locked": false, "security_level": "public" }
            ]
          },
          "alters": [
            {
              "id": 1,
              "name": "Alpha",
              "pronouns": "they/them",
              "description": "%DESC%",
              "color": "#abcdef",
              "avatar_url": "https://cdn.example/1.png",
              "proxy_name": "AlphaProxy",
              "discord_proxies": ["prefix;text suffix"],
              "fields": [
                { "id": "11111111-1111-1111-1111-111111111111", "value": "the-value" }
              ]
            },
            {
              "id": 2,
              "name": "Beta",
              "pronouns": null,
              "description": null,
              "color": null,
              "avatar_url": null,
              "proxy_name": null,
              "discord_proxies": [],
              "fields": []
            }
          ],
          "fronts": [
            {
              "id": "55555555-5555-5555-5555-555555555555",
              "alter_id": 1,
              "comment": "open front",
              "time_start": "2024-05-01T00:00:00+00:00",
              "time_end": null
            },
            {
              "id": "66666666-6666-6666-6666-666666666666",
              "alter_id": 2,
              "comment": "closed front",
              "time_start": "2024-04-01T00:00:00+00:00",
              "time_end": "2024-04-02T00:00:00+00:00"
            }
          ],
          "tags": [
            {
              "id": "22222222-2222-2222-2222-222222222222",
              "name": "RootTag",
              "description": "root description",
              "color": "#112233",
              "security_level": "public",
              "parent_tag_id": null,
              "inserted_at": "2024-01-02T03:04:05Z",
              "updated_at": "2024-06-07T08:09:10Z",
              "alters": [1, 2]
            },
            {
              "id": "33333333-3333-3333-3333-333333333333",
              "name": "ChildTag",
              "description": null,
              "color": null,
              "security_level": "public",
              "parent_tag_id": "22222222-2222-2222-2222-222222222222",
              "inserted_at": "2024-01-02T03:04:05Z",
              "updated_at": "2024-06-07T08:09:10Z",
              "alters": [1]
            }
          ],
          "polls": [
            {
              "id": "44444444-4444-4444-4444-444444444444",
              "title": "Poll Title",
              "description": "poll description",
              "type": "vote",
              "data": {"foo":"bar"},
              "time_end": "2024-12-31T23:59:59Z",
              "inserted_at": "2024-01-02T03:04:05Z",
              "updated_at": "2024-06-07T08:09:10Z"
            }
          ]
        }
        """.Replace("%DESC%", PkDescription1200);
    }

    public static JsonElement ParseData(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
