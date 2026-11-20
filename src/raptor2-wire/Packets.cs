using System.Buffers.Binary;
using System.Text;

namespace Raptor2.Wire;

public enum PacketType : byte { Data = 1, Status = 2, Have = 3 }

/// <summary>Client to VM datagram: a DATA symbol, or a STATUS query (no symbol).</summary>
public readonly ref struct ClientPacket
{
    /// <summary>
    /// Version 2: DATA packets may omit the key (keyLen 0). The sender includes it in a subset of packets
    /// (<see cref="CarriesKey"/>) and in every STATUS; a MicroVM holds a segment until it knows the key.
    /// </summary>
    public const byte Version = 2;
    public const int FixedBytes = 25;

    /// <summary>Which DATA packets carry the key: every 16th source symbol, every repair symbol, and all packets of small blocks.</summary>
    public static bool CarriesKey(int esi, int k) => k <= 16 || esi >= k || (esi & 15) == 0;
    public const int MaxDatagram = 1400;

    public PacketType Type { get; init; }
    public byte Slot { get; init; }
    public ReadOnlySpan<byte> TransferId { get; init; } // 8 bytes
    public int ObjectIdx { get; init; }
    public long Size { get; init; }
    public int Block { get; init; }
    public int Esi { get; init; }
    public ReadOnlySpan<byte> Key { get; init; }         // UTF-8
    public ReadOnlySpan<byte> Symbol { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> data, out ClientPacket packet)
    {
        packet = default;
        if (data.Length < FixedBytes) return false;
        if ((data[0] & 0x0F) != Version) return false;
        var type = (PacketType)(data[0] >> 4);
        if (type is not (PacketType.Data or PacketType.Status)) return false;
        int keyLen = data[24];
        if (data.Length < FixedBytes + keyLen) return false;
        if (keyLen == 0 && type == PacketType.Status) return false;
        long size = ((long)data[13] << 40) | ((long)data[14] << 32) | ((long)data[15] << 24) |
                    ((long)data[16] << 16) | ((long)data[17] << 8) | data[18];
        if (size > Layout.MaxObjectBytes) return false;
        packet = new ClientPacket
        {
            Type = type,
            Slot = data[1],
            TransferId = data.Slice(2, 8),
            ObjectIdx = (data[10] << 16) | (data[11] << 8) | data[12],
            Size = size,
            Block = (data[19] << 16) | (data[20] << 8) | data[21],
            Esi = (data[22] << 8) | data[23],
            Key = data.Slice(FixedBytes, keyLen),
            Symbol = data[(FixedBytes + keyLen)..],
        };
        return true;
    }

    /// <summary>Writes the header and key, followed by <paramref name="symbol"/>; returns the datagram length.</summary>
    public static int Write(Span<byte> d, PacketType type, byte slot, ReadOnlySpan<byte> transferId, int objectIdx,
        long size, int block, int esi, ReadOnlySpan<byte> key, ReadOnlySpan<byte> symbol)
    {
        d[0] = (byte)(((byte)type << 4) | Version);
        d[1] = slot;
        transferId[..8].CopyTo(d.Slice(2, 8));
        d[10] = (byte)(objectIdx >> 16); d[11] = (byte)(objectIdx >> 8); d[12] = (byte)objectIdx;
        d[13] = (byte)(size >> 40); d[14] = (byte)(size >> 32); d[15] = (byte)(size >> 24);
        d[16] = (byte)(size >> 16); d[17] = (byte)(size >> 8); d[18] = (byte)size;
        d[19] = (byte)(block >> 16); d[20] = (byte)(block >> 8); d[21] = (byte)block;
        d[22] = (byte)(esi >> 8); d[23] = (byte)esi;
        d[24] = (byte)key.Length;
        key.CopyTo(d[FixedBytes..]);
        symbol.CopyTo(d[(FixedBytes + key.Length)..]);
        return FixedBytes + key.Length + symbol.Length;
    }
}

/// <summary>VM to client reply: the blocks of an object that are held or committed, and whether it is complete.</summary>
public sealed class HavePacket
{
    public const byte Version = 1;
    public const int HeaderBytes = 21;
    public const int MaxRuns = 120;
    public const int MaxNeeds = 100;

    public byte[] TransferId { get; init; } = new byte[8];
    public int ObjectIdx { get; init; }
    public bool Complete { get; init; }
    /// <summary>When ScopeCount > 0 the runs are a full snapshot of blocks [ScopeStart, ScopeStart+ScopeCount): blocks not listed are not held.</summary>
    public int ScopeStart { get; init; }
    public int ScopeCount { get; init; }
    public List<(int Start, int Count)> Runs { get; init; } = [];
    /// <summary>
    /// STATUS replies only: for incomplete blocks in scope, how many more distinct symbols the MicroVM needs
    /// (K when it holds none). Appended after the runs as count(2) then block(3) need(2) entries; older parsers ignore it.
    /// </summary>
    public List<(int Block, int Need)> Needs { get; init; } = [];

    public byte[] ToBytes()
    {
        int n = Math.Min(Runs.Count, MaxRuns);
        int m = Math.Min(Needs.Count, MaxNeeds);
        var d = new byte[HeaderBytes + n * 6 + (m > 0 ? 2 + m * 5 : 0)];
        d[0] = (byte)(((byte)PacketType.Have << 4) | Version);
        TransferId.CopyTo(d, 1);
        d[9] = (byte)(ObjectIdx >> 16); d[10] = (byte)(ObjectIdx >> 8); d[11] = (byte)ObjectIdx;
        d[12] = (byte)(Complete ? 1 : 0);
        d[13] = (byte)(ScopeStart >> 16); d[14] = (byte)(ScopeStart >> 8); d[15] = (byte)ScopeStart;
        d[16] = (byte)(ScopeCount >> 16); d[17] = (byte)(ScopeCount >> 8); d[18] = (byte)ScopeCount;
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(19), (ushort)n);
        for (int i = 0; i < n; i++)
        {
            var (s, c) = Runs[i];
            int o = HeaderBytes + i * 6;
            d[o] = (byte)(s >> 16); d[o + 1] = (byte)(s >> 8); d[o + 2] = (byte)s;
            d[o + 3] = (byte)(c >> 16); d[o + 4] = (byte)(c >> 8); d[o + 5] = (byte)c;
        }
        if (m > 0)
        {
            int o = HeaderBytes + n * 6;
            BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(o), (ushort)m);
            for (int i = 0; i < m; i++)
            {
                var (b, need) = Needs[i];
                int p = o + 2 + i * 5;
                d[p] = (byte)(b >> 16); d[p + 1] = (byte)(b >> 8); d[p + 2] = (byte)b;
                BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(p + 3), (ushort)Math.Min(need, ushort.MaxValue));
            }
        }
        return d;
    }

    public static bool TryParse(ReadOnlySpan<byte> d, out HavePacket? packet)
    {
        packet = null;
        if (d.Length < HeaderBytes || (d[0] & 0x0F) != Version || (PacketType)(d[0] >> 4) != PacketType.Have) return false;
        int n = BinaryPrimitives.ReadUInt16BigEndian(d[19..]);
        if (d.Length < HeaderBytes + n * 6) return false;
        var runs = new List<(int, int)>(n);
        for (int i = 0; i < n; i++)
        {
            int o = HeaderBytes + i * 6;
            runs.Add(((d[o] << 16) | (d[o + 1] << 8) | d[o + 2], (d[o + 3] << 16) | (d[o + 4] << 8) | d[o + 5]));
        }
        var needs = new List<(int, int)>();
        int at = HeaderBytes + n * 6;
        if (d.Length >= at + 2)
        {
            int m = BinaryPrimitives.ReadUInt16BigEndian(d[at..]);
            if (d.Length < at + 2 + m * 5) return false;
            for (int i = 0; i < m; i++)
            {
                int p = at + 2 + i * 5;
                needs.Add(((d[p] << 16) | (d[p + 1] << 8) | d[p + 2], BinaryPrimitives.ReadUInt16BigEndian(d[(p + 3)..])));
            }
        }
        packet = new HavePacket
        {
            TransferId = d.Slice(1, 8).ToArray(),
            ObjectIdx = (d[9] << 16) | (d[10] << 8) | d[11],
            Complete = (d[12] & 1) != 0,
            ScopeStart = (d[13] << 16) | (d[14] << 8) | d[15],
            ScopeCount = (d[16] << 16) | (d[17] << 8) | d[18],
            Runs = runs,
            Needs = needs,
        };
        return true;
    }

    /// <summary>Collapses a sorted set of block numbers into runs.</summary>
    public static List<(int Start, int Count)> ToRuns(IEnumerable<int> sortedBlocks)
    {
        var runs = new List<(int, int)>();
        int start = -1, count = 0;
        foreach (var b in sortedBlocks)
        {
            if (count > 0 && b == start + count) count++;
            else
            {
                if (count > 0) runs.Add((start, count));
                start = b; count = 1;
            }
        }
        if (count > 0) runs.Add((start, count));
        return runs;
    }
}

public static class Keys
{
    public static bool TryEncode(string key, out byte[] bytes)
    {
        bytes = Encoding.UTF8.GetBytes(key);
        return bytes.Length is > 0 and <= Layout.MaxKeyBytes;
    }
}
