namespace Pdp.ControlPlane.Ipam;

/// <summary>
/// The ledger's bootstrap seed (data-model.md §5): the platform-shared supernet
/// (<c>region_index = 0</c>) and the control-plane VNet recorded as a non-allocatable
/// reservation, so the platform's own VNet range is registered the moment the schema exists
/// (Article VI, research §12).
/// <para>
/// Single source of truth, applied in two places: the initial migration (for a freshly
/// migrated DB — live or container) and the test fixture's post-reset re-seed (Respawn wipes
/// all rows, so the baseline is re-applied). The SQL is idempotent (<c>ON CONFLICT DO
/// NOTHING</c>) so running it against an already-seeded DB is a no-op.
/// </para>
/// </summary>
public static class IpamSeedData
{
    /// <summary>Deterministic id of the platform-shared pool (index 0).</summary>
    public const string PlatformPoolId = "11111111-1111-1111-1111-111111111111";

    /// <summary>Deterministic id of the control-plane VNet reservation.</summary>
    public const string ControlPlaneVnetAllocationId = "22222222-2222-2222-2222-222222222222";

    /// <summary>The platform-shared supernet (data-model §1).</summary>
    public const string PlatformSupernet = "10.0.0.0/16";

    /// <summary>The control-plane VNet's seeded reservation range (data-model §1).</summary>
    public const string ControlPlaneVnetCidr = "10.0.0.0/24";

    /// <summary>The reservation's caller-visible name (contract / quickstart Scenario 6).</summary>
    public const string ControlPlaneVnetName = "control-plane-vnet";

    /// <summary>Fixed audit timestamp so the seed is deterministic across environments.</summary>
    private const string SeededAt = "2026-01-01T00:00:00+00:00";

    /// <summary>
    /// Idempotent INSERTs for the platform pool and the control-plane VNet reservation. Safe to
    /// run against a fresh or an already-seeded database.
    /// </summary>
    public static string Sql { get; } = $"""
        INSERT INTO {IpamDbContext.Schema}.region_pool (id, region, region_index, supernet, hub_carveout, created_at)
        VALUES ('{PlatformPoolId}', 'platform', 0, '{PlatformSupernet}', NULL, '{SeededAt}')
        ON CONFLICT (id) DO NOTHING;

        INSERT INTO {IpamDbContext.Schema}.allocation (id, pool_id, name, network, prefix_length, kind, allocated_at)
        VALUES ('{ControlPlaneVnetAllocationId}', '{PlatformPoolId}', '{ControlPlaneVnetName}',
                '{ControlPlaneVnetCidr}', 24, 'reservation', '{SeededAt}')
        ON CONFLICT (id) DO NOTHING;
        """;
}
