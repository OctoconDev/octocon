using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Shared.TestServices;

/// <summary>
/// Factory helpers for creating <see cref="HttpClient"/> instances with common options.
/// Centralises the <c>new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }</c>
/// boilerplate that appeared at 42 sites across the integration-test suite.
/// </summary>
public static class TestClient
{
    private static readonly WebApplicationFactoryClientOptions NoRedirectOptions = new()
    {
        AllowAutoRedirect = false,
    };

    /// <summary>
    /// Creates an <see cref="HttpClient"/> from the session-shared factory with
    /// <c>AllowAutoRedirect = false</c>.
    /// </summary>
    public static HttpClient NoRedirect(IWebFactoryFixture fixture)
        => fixture.Factory.CreateClient(NoRedirectOptions);

    /// <summary>
    /// Creates an <see cref="HttpClient"/> from a caller-owned factory with
    /// <c>AllowAutoRedirect = false</c>. Use when the test builds its own
    /// <see cref="InterfoldWebApplicationFactory"/> in-body.
    /// </summary>
    public static HttpClient NoRedirect(InterfoldWebApplicationFactory factory)
        => factory.CreateClient(NoRedirectOptions);
}
