namespace Pdp.ControlPlane.Ipam.Tests;

/// <summary>
/// xUnit collection that shares one <see cref="PostgresFixture"/> (one container) across every
/// integration test class, while data is reset per test. Derive test classes from
/// <see cref="IpamIntegrationTest"/> to join it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class IpamCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "ipam-postgres";
}
