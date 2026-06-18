using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Pdp.ControlPlane.TestSupport;

/// <summary>
/// A configurable <see cref="WebApplicationFactory{TEntryPoint}"/> base for the API host tests
/// (webhook endpoint HMAC + typed <c>workflow_run</c>, end-to-end verb→dispatch→track). It injects
/// test configuration (the Testcontainers Postgres connection, the fake-GitHub base URL, the webhook
/// secret) ahead of the host's own configuration so the host binds to the test dependencies.
/// </summary>
/// <typeparam name="TEntryPoint">The API host's entry point (its <c>Program</c>).</typeparam>
public class PdpWebApplicationFactory<TEntryPoint> : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    private readonly IReadOnlyDictionary<string, string?> _configuration;

    /// <summary>Creates the factory with configuration overrides applied to the host under test.</summary>
    /// <param name="configuration">Key/value pairs layered over the host's configuration.</param>
    public PdpWebApplicationFactory(IReadOnlyDictionary<string, string?>? configuration = null)
        => _configuration = configuration ?? new Dictionary<string, string?>();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(_configuration));
}
