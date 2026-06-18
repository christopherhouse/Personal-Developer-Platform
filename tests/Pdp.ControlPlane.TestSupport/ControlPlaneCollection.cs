namespace Pdp.ControlPlane.TestSupport;

/// <summary>
/// xUnit collection that shares one <see cref="ControlPlanePostgresFixture"/> (one container) across
/// every control-plane integration test class that joins it, while data is reset per test. Test
/// classes opt in with <c>[Collection(ControlPlaneCollection.Name)]</c>.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ControlPlaneCollection : ICollectionFixture<ControlPlanePostgresFixture>
{
    /// <summary>The shared collection name.</summary>
    public const string Name = "control-plane-postgres";
}
