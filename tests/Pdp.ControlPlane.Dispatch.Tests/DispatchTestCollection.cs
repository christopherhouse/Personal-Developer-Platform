using Pdp.ControlPlane.TestSupport;

namespace Pdp.ControlPlane.Dispatch.Tests;

/// <summary>
/// Binds this assembly's tests to the shared <see cref="ControlPlanePostgresFixture"/> (one container,
/// data reset per test). xUnit requires the <c>[CollectionDefinition]</c> in the test assembly.
/// </summary>
[CollectionDefinition(ControlPlaneCollection.Name)]
public sealed class DispatchTestCollection : ICollectionFixture<ControlPlanePostgresFixture>;
