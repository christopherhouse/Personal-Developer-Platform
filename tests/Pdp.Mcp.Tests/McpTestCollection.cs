using Pdp.ControlPlane.TestSupport;

namespace Pdp.Mcp.Tests;

/// <summary>
/// Binds the MCP host tests that need a real database to the shared <see cref="ControlPlanePostgresFixture"/>
/// (one Testcontainers Postgres, started once per run). xUnit requires the <c>[CollectionDefinition]</c> to
/// live in this assembly. The pure adapter / token tests do not join the collection — they need no database.
/// </summary>
[CollectionDefinition(Name)]
public sealed class McpTestCollection : ICollectionFixture<ControlPlanePostgresFixture>
{
    /// <summary>The shared collection name.</summary>
    public const string Name = "mcp-postgres";
}
