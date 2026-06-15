using System.Buffers.Binary;
using System.Net;
using System.Numerics;

namespace Pdp.ControlPlane.Ipam.Allocator;

/// <summary>
/// Computes the free space remaining in a pool for the read-only <c>query</c> projection
/// (contracts/ipam-operations.md): the pool's supernet minus its reserved hub carve-out minus
/// every live allocation, expressed both as a total free-address count and as the maximal aligned
/// CIDR blocks that remain allocatable. Pure and deterministic — the sibling of
/// <see cref="FirstFitAllocator"/>, which picks a single block; this one reports the whole
/// remainder.
/// <para>
/// IPv4 only — the platform's address space is entirely within <c>10.0.0.0/8</c>.
/// </para>
/// </summary>
public static class FreeSpaceCalculator
{
    /// <summary>
    /// Returns the free address count and the maximal aligned free CIDR blocks within
    /// <paramref name="supernet"/>, excluding the <paramref name="hubCarveout"/> and every block
    /// in <paramref name="existing"/>. Blocks are returned in ascending address order; their sizes
    /// sum to the returned count.
    /// </summary>
    public static (ulong AddressCount, IReadOnlyList<IPNetwork> Blocks) Compute(
        IPNetwork supernet,
        IPNetwork? hubCarveout,
        IReadOnlyCollection<IPNetwork> existing)
    {
        var (poolStart, poolEnd) = ToRange(supernet);

        // Everything off-limits — the carve-out plus the taken blocks. They never overlap (the DB
        // exclusion constraint guarantees it), so a sort is enough to walk the gaps between them.
        var occupied = new List<(ulong Start, ulong End)>(existing.Count + 1);
        if (hubCarveout is { } carveout)
        {
            occupied.Add(ToRange(carveout));
        }

        foreach (var block in existing)
        {
            occupied.Add(ToRange(block));
        }

        occupied.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Walk the free gaps between occupied runs in ascending order, decomposing each into
        // maximal aligned CIDR blocks.
        var blocks = new List<IPNetwork>();
        ulong cursor = poolStart;
        foreach (var (start, end) in occupied)
        {
            if (start > cursor)
            {
                AppendBlocks(blocks, cursor, Math.Min(start, poolEnd));
            }

            cursor = Math.Max(cursor, end);
            if (cursor >= poolEnd)
            {
                break;
            }
        }

        if (cursor < poolEnd)
        {
            AppendBlocks(blocks, cursor, poolEnd);
        }

        ulong count = 0;
        foreach (var block in blocks)
        {
            count += 1UL << (32 - block.PrefixLength);
        }

        return (count, blocks);
    }

    // Decompose the half-open address range [start, end) into the fewest maximal CIDR blocks, each
    // aligned to its own size — the standard range-to-CIDR split.
    private static void AppendBlocks(List<IPNetwork> blocks, ulong start, ulong end)
    {
        while (start < end)
        {
            // The largest block the start address is aligned to (its trailing-zero run), capped by
            // the largest power of two that still fits in the remaining range.
            ulong byAlignment = start == 0 ? 1UL << 32 : 1UL << BitOperations.TrailingZeroCount(start);
            ulong byRemaining = 1UL << BitOperations.Log2(end - start);
            ulong size = Math.Min(byAlignment, byRemaining);

            blocks.Add(new IPNetwork(ToAddress(start), 32 - BitOperations.Log2(size)));
            start += size;
        }
    }

    private static (ulong Start, ulong End) ToRange(IPNetwork network)
    {
        ulong start = ToUInt32(network.BaseAddress);
        ulong size = 1UL << (32 - network.PrefixLength);
        return (start, start + size);
    }

    private static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out int written) || written != 4)
        {
            throw new ArgumentException("An IPv4 address is required.", nameof(address));
        }

        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static IPAddress ToAddress(ulong value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value);
        return new IPAddress(bytes);
    }
}
