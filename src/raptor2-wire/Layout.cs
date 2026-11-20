using System.Numerics;

namespace Raptor2.Wire;

/// <summary>
/// How an object of a given size is cut into blocks, symbols and segments. A pure function of the object
/// size, computed identically by the client and the MicroVM, so packets never carry any of it.
/// </summary>
public readonly record struct Layout
{
    public const int BlockBytes = 512 * 1024;
    public const int MaxSymbolBytes = 1280;
    public const int LargeSymbolBytes = 1024;      // BlockBytes / 512
    public const int LargeK = BlockBytes / LargeSymbolBytes;
    public const int MinSegmentShift = 5;          // 32 blocks = 16 MiB, above S3's 5 MiB part minimum
    public const int MaxParts = 9000;              // S3 allows 10,000; leave headroom
    public const long MaxObjectBytes = 5L * 1024 * 1024 * 1024 * 1024; // S3's object size limit
    public const int MaxKeyBytes = 95;

    public long Size { get; }
    public int Blocks { get; }
    public int SymbolBytes { get; }
    public int SegmentShift { get; }
    public int Segments { get; }

    private Layout(long size, int blocks, int symbolBytes, int shift, int segments)
    {
        Size = size; Blocks = blocks; SymbolBytes = symbolBytes; SegmentShift = shift; Segments = segments;
    }

    public static Layout For(long size)
    {
        if (size < 0 || size > MaxObjectBytes) throw new ArgumentOutOfRangeException(nameof(size));
        int blocks = (int)Math.Max(1, (size + BlockBytes - 1) / BlockBytes);
        int symbol;
        if (size <= BlockBytes)
        {
            int k = (int)Math.Max(1, (size + MaxSymbolBytes - 1) / MaxSymbolBytes);
            symbol = (int)((size + k - 1) / k);
        }
        else symbol = LargeSymbolBytes;

        int shift = MinSegmentShift;
        long perSegment = 1L << shift;
        while ((blocks + perSegment - 1) / perSegment > MaxParts) { shift++; perSegment <<= 1; }
        int segments = (int)((blocks + perSegment - 1) / perSegment);
        return new(size, blocks, symbol, shift, segments);
    }

    public bool IsSinglePart => Segments == 1;
    public int BlocksPerSegment => 1 << SegmentShift;

    public int SegmentOf(int block) => block >> SegmentShift;
    public int FirstBlockOf(int segment) => segment << SegmentShift;
    public int BlocksIn(int segment) => Math.Min(BlocksPerSegment, Blocks - FirstBlockOf(segment));

    /// <summary>Object bytes carried by <paramref name="block"/> (the last block may be short).</summary>
    public int BlockDataBytes(int block) =>
        block < Blocks - 1 ? BlockBytes : (int)(Size - (long)(Blocks - 1) * BlockBytes);

    /// <summary>Source symbols in <paramref name="block"/>.</summary>
    public int SymbolsIn(int block)
    {
        int bytes = BlockDataBytes(block);
        return bytes == 0 ? 1 : (bytes + SymbolBytes - 1) / SymbolBytes;
    }

    /// <summary>Bytes the symbols of <paramref name="block"/> occupy, including zero padding of the last symbol.</summary>
    public int PaddedBlockBytes(int block) => SymbolsIn(block) * SymbolBytes;

    /// <summary>Object bytes in a segment (what is uploaded as one S3 part).</summary>
    public long SegmentDataBytes(int segment)
    {
        long start = (long)FirstBlockOf(segment) * BlockBytes;
        long end = Math.Min(Size, (long)(FirstBlockOf(segment) + BlocksIn(segment)) * BlockBytes);
        return end - start;
    }

    /// <summary>Buffer length that holds every block of the segment including padding.</summary>
    public long SegmentBufferBytes(int segment)
    {
        int first = FirstBlockOf(segment);
        int count = BlocksIn(segment);
        return (long)(count - 1) * BlockBytes + PaddedBlockBytes(first + count - 1);
    }

    /// <summary>Offset of a block inside its segment buffer.</summary>
    public long OffsetInSegment(int block) => (long)(block - FirstBlockOf(SegmentOf(block))) * BlockBytes;

    /// <summary>Repair symbols to send up front for a block: ceil(K * overhead), at least 1.</summary>
    public static int RepairCount(int k, double overhead) => Math.Max(1, (int)Math.Ceiling(k * overhead));

    /// <summary>The number of lanes (segments in flight) that a send rate needs.</summary>
    public static int LanesForRate(double mbps) => Math.Max(1, (int)Math.Ceiling(mbps / Route.MbpsPerSlot));
}

/// <summary>Picks the tenant slot for a block. Shared by the client (which writes it) and the VM (which checks it).</summary>
public static class Route
{
    public const int SmallSlots = 4;
    public const int LargeSlots = 60;
    public const int Slots = SmallSlots + LargeSlots;
    public const double MbpsPerSlot = 300;

    public static uint Salt(ReadOnlySpan<byte> transferId)
    {
        // FNV-1a; stable across platforms, which GetHashCode is not.
        uint h = 2166136261;
        foreach (var b in transferId) { h ^= b; h *= 16777619; }
        return h;
    }

    /// <summary>
    /// The slot (tenant) for a block. <paramref name="small"/> slots 0..small-1 take single-part objects; the next
    /// <paramref name="large"/> slots take the parts of multipart objects. The MicroVMs accept any slot, so the pool
    /// size is the client's choice: each slot in use is one MicroVM, which has to fit the account's MicroVM memory quota.
    /// </summary>
    public static byte Slot(ReadOnlySpan<byte> transferId, int objectIdx, in Layout layout, int block,
        int small = SmallSlots, int large = LargeSlots)
    {
        if (small < 1 || large < 1 || small + large > 256) throw new ArgumentOutOfRangeException(nameof(small));
        uint salt = Salt(transferId);
        if (layout.IsSinglePart) return (byte)((salt + (uint)objectIdx) % (uint)small);
        return (byte)(small + (salt + (uint)layout.SegmentOf(block)) % (uint)large);
    }
}
