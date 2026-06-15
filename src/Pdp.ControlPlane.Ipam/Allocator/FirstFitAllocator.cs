using System.Buffers.Binary;
using System.Net;

namespace Pdp.ControlPlane.Ipam.Allocator;

/// <summary>
/// The first-fit, lowest-aligned free-block allocator (research §9). Pure and deterministic:
/// given a pool's supernet, its reserved hub carve-out, and the blocks already taken, it returns
/// the lowest properly-aligned free block of the requested prefix size, or <c>null</c> when none
/// remains. The ledger calls this inside the per-pool advisory-locked transaction; the database
/// GiST exclusion constraint is the ultimate backstop against overlap.
/// <para>
/// IPv4 only — the platform's address space is entirely within <c>10.0.0.0/8</c>.
/// </para>
/// </summary>
public static class FirstFitAllocator
{
    /// <summary>
    /// Finds the lowest aligned free <paramref name="prefixLength"/>-sized block within
    /// <paramref name="supernet"/>, excluding the <paramref name="hubCarveout"/> and every block
    /// in <paramref name="existing"/>.
    /// </summary>
    /// <returns>The free block, or <c>null</c> if the pool is exhausted for that size.</returns>
    public static IPNetwork? FindFreeBlock(
        IPNetwork supernet,
        IPNetwork? hubCarveout,
        IReadOnlyCollection<IPNetwork> existing,
        int prefixLength)
    {
        var (poolStart, poolEnd) = ToRange(supernet);
        ulong blockSize = 1UL << (32 - prefixLength);

        // Collect everything that is off-limits — the carve-out plus the taken blocks — then
        // coalesce so the walk only has to step over maximal occupied runs.
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

        // Walk the free gaps between occupied runs in ascending order; the first gap that admits
        // an aligned block wins (first-fit lowest). Block size is a power of two and the supernet
        // is /16-aligned, so aligning the cursor up to blockSize yields a valid candidate start.
        ulong cursor = poolStart;
        foreach (var (start, end) in occupied)
        {
            if (start >= poolEnd)
            {
                break;
            }

            ulong gapEnd = Math.Min(start, poolEnd);
            ulong candidate = AlignUp(cursor, blockSize);
            if (candidate + blockSize <= gapEnd)
            {
                return new IPNetwork(ToAddress(candidate), prefixLength);
            }

            cursor = Math.Max(cursor, Math.Min(end, poolEnd));
            if (cursor >= poolEnd)
            {
                return null;
            }
        }

        // The final gap from the last occupied run to the end of the supernet.
        ulong tail = AlignUp(cursor, blockSize);
        return tail + blockSize <= poolEnd ? new IPNetwork(ToAddress(tail), prefixLength) : null;
    }

    private static (ulong Start, ulong End) ToRange(IPNetwork network)
    {
        ulong start = ToUInt32(network.BaseAddress);
        ulong size = 1UL << (32 - network.PrefixLength);
        return (start, start + size);
    }

    // alignment is a power of two, so round up by masking off the low bits.
    private static ulong AlignUp(ulong value, ulong alignment)
        => (value + alignment - 1) & ~(alignment - 1);

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
