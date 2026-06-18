using Pdp.ControlPlane.TestSupport;

namespace Pdp.ControlPlane.Registry.Tests;

/// <summary>
/// Binds this assembly's tests to the shared <see cref="ControlPlanePostgresFixture"/> (one container,
/// data reset per test). xUnit requires the <c>[CollectionDefinition]</c> to live in the same assembly
/// as the tests that use it, so each control-plane test project re-declares it against the shared
/// fixture type under the same collection name.
/// </summary>
[CollectionDefinition(ControlPlaneCollection.Name)]
public sealed class RegistryTestCollection : ICollectionFixture<ControlPlanePostgresFixture>;
