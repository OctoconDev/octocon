using System.Net;
using System.Text.Json;
using Interfold.Shared.Contracts;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Controllers;

// GET /api/settings/export coverage — mirrors the Elixir bot's
// OctoconDiscord.Commands.Export flow (see octocon/lib/octocon_discord/commands/export.ex):
// enum-driven format selection, raw JSON body (no Response<T> envelope), and
// Content-Disposition stamped with the wire spelling of the format.
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public sealed class SettingsExportEndpointTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task Export_MissingFormat_ReturnsBadRequest()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "export-missing-format";
        await EnsureUserExistsAsync(client, principal);

        using var res = await client.SendAuthedGetAsync("/api/settings/export", principal);
        var body = await res.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.BadRequest)
                .Because($"Expected 400 for missing ?format; body: {body}");
            await Assert.That(body).Contains(ErrorCodes.InvalidExportFormat.Value)
                .Because($"Expected error body to carry the invalid_export_format code; body: {body}");
        }
    }

    [Test]
    public async Task Export_UnknownFormat_ReturnsBadRequest()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "export-unknown-format";
        await EnsureUserExistsAsync(client, principal);

        // ASP.NET Core's enum model binder is case-insensitive but rejects unrecognised names.
        // The [ApiController] filter surfaces the failure as 400 before entering the action.
        using var res = await client.SendAuthedGetAsync("/api/settings/export?format=xyz", principal);
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Export_PkFormat_ReturnsAttachmentWithVersion2Body()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "export-pk-happy";
        await EnsureUserExistsAsync(client, principal);
        _ = await CreateAlterAsync(client, principal, "AlphaAlter");

        using var res = await client.SendAuthedGetAsync("/api/settings/export?format=pk", principal);
        var body = await res.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.OK)
                .Because($"Expected 200 for ?format=pk; body: {body}");
            await Assert.That(res.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
            await Assert.That(res.Content.Headers.ContentDisposition?.DispositionType).IsEqualTo("attachment");
            await Assert.That(res.Content.Headers.ContentDisposition?.FileName).IsEqualTo("octocon_export_pk.json");

            using var doc = JsonDocument.Parse(body);
            await Assert.That(doc.RootElement.GetProperty("version").GetInt32()).IsEqualTo(2)
                .Because("PluralKit v2 importable payload MUST advertise version=2 or PK will refuse the file.");
            await Assert.That(doc.RootElement.GetProperty("members").ValueKind).IsEqualTo(JsonValueKind.Array);
        }
    }

    [Test]
    public async Task Export_FullFormat_ReturnsAttachmentWithFullBody()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "export-full-happy";
        await EnsureUserExistsAsync(client, principal);
        _ = await CreateAlterAsync(client, principal, "AlphaAlter");

        using var res = await client.SendAuthedGetAsync("/api/settings/export?format=full", principal);
        var body = await res.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.OK)
                .Because($"Expected 200 for ?format=full; body: {body}");
            await Assert.That(res.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
            await Assert.That(res.Content.Headers.ContentDisposition?.DispositionType).IsEqualTo("attachment");
            await Assert.That(res.Content.Headers.ContentDisposition?.FileName).IsEqualTo("octocon_export_full.json");

            using var doc = JsonDocument.Parse(body);
            await Assert.That(doc.RootElement.TryGetProperty("user", out _)).IsTrue();
            await Assert.That(doc.RootElement.TryGetProperty("alters", out _)).IsTrue();
            await Assert.That(doc.RootElement.TryGetProperty("fronts", out _)).IsTrue();
            await Assert.That(doc.RootElement.TryGetProperty("tags", out _)).IsTrue();
            await Assert.That(doc.RootElement.TryGetProperty("polls", out _)).IsTrue();
        }
    }

    [Test]
    public async Task Export_PkFormat_IsCaseInsensitive()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "export-pk-case";
        await EnsureUserExistsAsync(client, principal);

        using var res = await client.SendAuthedGetAsync("/api/settings/export?format=PK", principal);
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.OK)
            .Because("Enum model binder is case-insensitive so ?format=PK binds to ExportFormat.Pk.");
    }
}
