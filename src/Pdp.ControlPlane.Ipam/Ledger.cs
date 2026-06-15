using Microsoft.EntityFrameworkCore;
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
}
