using System.Buffers.Binary;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pdp.ControlPlane.Ipam.Allocator;
using Pdp.ControlPlane.Ipam.Entities;

namespace Pdp.ControlPlane.Ipam;

/// <summary>
/// The IPAM ledger operations against the control-plane Postgres (data-model.md, research §9).
/// Each mutating call runs in an explicit transaction guarded by a per-pool
/// <c>pg_advisory_xact_lock</c>, so concurrent callers serialize per pool while parallel across
/// pools; the <c>allocations_no_overlap</c> GiST exclusion constraint is the database backstop
/// that makes overlap impossible even if a lock were ever bypassed (FR-006, FR-008).
/// <para>
/// The context is expected to be scoped per unit of work (one per request/task) — it is not
/// shared across concurrent callers, exactly as EF Core requires.
/// </para>
/// </summary>
public sealed class Ledger(IpamDbContext context) : IIpamLedger
{
    /// <summary>Default spoke block size when the caller does not specify one (data-model §1).</summary>
    public const int DefaultPrefixLength = 24;

    /// <summary>Largest permitted spoke block: <c>/22</c> (the smallest prefix-length number).</summary>
    public const int LargestBlockPrefixLength = 22;

    /// <summary>Smallest permitted spoke block: <c>/29</c> (the largest prefix-length number).</summary>
    public const int SmallestBlockPrefixLength = 29;

    /// <summary>Lowest geographic region index (index 0 is the platform-shared pool).</summary>
    public const int MinRegionIndex = 1;

    /// <summary>Highest region index — the second octet of <c>10.R.0.0/16</c>.</summary>
    public const int MaxRegionIndex = 255;

    /// <inheritdoc />
    public async Task<RegionPool> RegisterRegionAsync(
        string region,
        int regionIndex,
        CancellationToken cancellationToken = default)
    {
        if (regionIndex < MinRegionIndex || regionIndex > MaxRegionIndex)
        {
            throw new InvalidRegionIndexException(regionIndex);
        }

        // The /16 and its top /22 are derived purely from the index (data-model §1); register is
        // the single place this convention is materialised.
        var supernet = IPNetwork.Parse($"10.{regionIndex}.0.0/16");
        var hubCarveout = IPNetwork.Parse($"10.{regionIndex}.252.0/22");

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Serialize same-region registrations so the existence checks below are race-safe; the
        // supernet exclusion constraint remains the backstop for two *different* regions racing on
        // the same index (mapped to SupernetOverlap in the catch). The lock releases at COMMIT.
        await LockRegionAsync(region, cancellationToken);

        var existing = await context.RegionPools
            .SingleOrDefaultAsync(p => p.Region == region, cancellationToken);
        if (existing is not null)
        {
            // Idempotent re-register with the same index is a no-op success; a different index is a
            // conflict — a region's /16 is fixed at registration.
            if (existing.RegionIndex != regionIndex)
            {
                throw new RegionAlreadyExistsException(region, existing.RegionIndex, regionIndex);
            }

            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        // Friendly path: a different region already owns this index (hence this supernet).
        var indexOwner = await context.RegionPools
            .SingleOrDefaultAsync(p => p.RegionIndex == regionIndex, cancellationToken);
        if (indexOwner is not null)
        {
            throw new SupernetOverlapException(region, regionIndex, indexOwner.Region);
        }

        var pool = new RegionPool
        {
            Id = Guid.NewGuid(),
            Region = region,
            RegionIndex = (short)regionIndex,
            Supernet = supernet,
            HubCarveout = hubCarveout,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        context.RegionPools.Add(pool);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            ConstraintName: "region_pool_supernet_no_overlap" or "ix_region_pool_region_index",
        })
        {
            // The DB backstop fired (FR-011): a concurrent caller took this index first.
            throw new SupernetOverlapException(region, regionIndex, conflictingRegion: null, ex);
        }

        await transaction.CommitAsync(cancellationToken);
        return pool;
    }

    /// <inheritdoc />
    public async Task<Allocation> AllocateAsync(
        string region,
        string name,
        int prefixLength = DefaultPrefixLength,
        CancellationToken cancellationToken = default)
    {
        if (prefixLength < LargestBlockPrefixLength || prefixLength > SmallestBlockPrefixLength)
        {
            throw new InvalidPrefixLengthException(prefixLength);
        }

        // The transaction holds the advisory lock until COMMIT/ROLLBACK; disposing it on any
        // thrown path rolls back, so PoolExhausted/NameConflict record nothing (FR-007).
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var pool = await context.RegionPools.SingleOrDefaultAsync(p => p.Region == region, cancellationToken)
            ?? throw new RegionNotRegisteredException(region);

        await LockPoolAsync(pool.Id, cancellationToken);

        // Idempotency (FR-010): the same (pool, name) returns the existing block unchanged; the
        // same name for a different size is a conflict, never a silent second allocation.
        var existing = await context.Allocations
            .SingleOrDefaultAsync(a => a.PoolId == pool.Id && a.Name == name, cancellationToken);
        if (existing is not null)
        {
            if (existing.PrefixLength != prefixLength)
            {
                throw new AllocationNameConflictException(region, name, existing.PrefixLength, prefixLength);
            }

            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        // Under the lock, the set of taken blocks is stable, so first-fit is deterministic.
        var taken = await context.Allocations
            .Where(a => a.PoolId == pool.Id)
            .Select(a => a.Network)
            .ToListAsync(cancellationToken);

        var block = FirstFitAllocator.FindFreeBlock(pool.Supernet, pool.HubCarveout, taken, prefixLength)
            ?? throw new PoolExhaustedException(region, prefixLength);

        var allocation = new Allocation
        {
            Id = Guid.NewGuid(),
            PoolId = pool.Id,
            Name = name,
            Network = block,
            PrefixLength = (short)prefixLength,
            Kind = AllocationKind.Spoke,
            AllocatedAt = DateTimeOffset.UtcNow,
        };

        context.Allocations.Add(allocation);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return allocation;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string region, string name, CancellationToken cancellationToken = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var pool = await context.RegionPools.SingleOrDefaultAsync(p => p.Region == region, cancellationToken)
            ?? throw new RegionNotRegisteredException(region);

        await LockPoolAsync(pool.Id, cancellationToken);

        var existing = await context.Allocations
            .SingleOrDefaultAsync(a => a.PoolId == pool.Id && a.Name == name, cancellationToken);
        if (existing is null)
        {
            // Clean idempotent no-op — never a double-free that could re-hand-out the space (FR-009).
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (existing.Kind == AllocationKind.Reservation)
        {
            throw new CannotReleaseReservationException(region, name);
        }

        context.Allocations.Remove(existing);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RegionView> QueryAsync(string region, CancellationToken cancellationToken = default)
    {
        // Read-only: no transaction or advisory lock — query reports current truth (contract, SC-005).
        var pool = await context.RegionPools
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Region == region, cancellationToken)
            ?? throw new RegionNotRegisteredException(region);

        var allocations = await context.Allocations
            .AsNoTracking()
            .Where(a => a.PoolId == pool.Id)
            .ToListAsync(cancellationToken);

        return ToView(pool, allocations);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegionView>> QueryAllAsync(CancellationToken cancellationToken = default)
    {
        var pools = await context.RegionPools
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // One pass over allocations, grouped by pool, rather than a query per pool.
        var allocationsByPool = (await context.Allocations
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .GroupBy(a => a.PoolId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Allocation>)g.ToList());

        return pools
            .OrderBy(p => p.RegionIndex)
            .Select(p => ToView(p, allocationsByPool.TryGetValue(p.Id, out var a) ? a : []))
            .ToList();
    }

    /// <summary>
    /// Projects a pool and its live allocations into the read-only <see cref="RegionView"/>:
    /// allocations in ascending address order, plus the free space remaining (supernet − carve-out
    /// − allocations) from <see cref="FreeSpaceCalculator"/>.
    /// </summary>
    private static RegionView ToView(RegionPool pool, IReadOnlyList<Allocation> allocations)
    {
        var ordered = allocations.OrderBy(a => ToOrderKey(a.Network)).ToList();

        var (freeAddresses, freeBlocks) = FreeSpaceCalculator.Compute(
            pool.Supernet,
            pool.HubCarveout,
            ordered.Select(a => a.Network).ToList());

        var views = ordered
            .Select(a => new AllocationView(a.Name, a.Network, a.PrefixLength, a.Kind, a.AllocatedAt))
            .ToList();

        return new RegionView(
            pool.Region,
            pool.RegionIndex,
            pool.Supernet,
            pool.HubCarveout,
            views,
            new FreeSpace(freeAddresses, freeBlocks));
    }

    // Stable ascending sort key for a block: its base address as a big-endian uint.
    private static uint ToOrderKey(IPNetwork network)
    {
        Span<byte> bytes = stackalloc byte[4];
        network.BaseAddress.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    /// <summary>
    /// Takes the transaction-scoped advisory lock for a pool. The key is a stable
    /// <see cref="long"/> derived from the pool id, so different pools allocate in parallel while
    /// same-pool callers serialize (research §9). The lock auto-releases at COMMIT/ROLLBACK.
    /// </summary>
    private Task LockPoolAsync(Guid poolId, CancellationToken cancellationToken)
    {
        long key = BitConverter.ToInt64(poolId.ToByteArray(), 0);
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }

    /// <summary>
    /// Takes the transaction-scoped advisory lock for a region name (used by registration, where
    /// no pool id exists yet). <c>hashtextextended</c> yields a stable <c>bigint</c> so concurrent
    /// registrations of the same region serialize while different regions proceed in parallel.
    /// </summary>
    private Task LockRegionAsync(string region, CancellationToken cancellationToken)
        => context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({region}, 0))", cancellationToken);
}
