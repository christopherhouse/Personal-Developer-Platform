namespace Pdp.ControlPlane.Ipam.Tests;

/// <summary>
/// Base for IPAM integration tests: joins the shared-container collection and resets the
/// database to the freshly-migrated baseline (data wiped, bootstrap seed restored) before each
/// test runs.
/// </summary>
[Collection(IpamCollection.Name)]
public abstract class IpamIntegrationTest(PostgresFixture fixture) : IAsyncLifetime
{
    /// <summary>The shared real-Postgres fixture.</summary>
    protected PostgresFixture Fixture { get; } = fixture;

    /// <inheritdoc />
    public Task InitializeAsync() => Fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;
}
