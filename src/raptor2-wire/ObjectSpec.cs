using System.Text;
using Proxylity.RaptorQ;

namespace Raptor2.Wire;

/// <summary>Everything the sender needs to know about one object: its layout, key, and how to address its packets.</summary>
public sealed class ObjectSpec
{
    public ObjectSpec(byte[] transferId, int objectIdx, string key, long size, int smallSlots = Route.SmallSlots, int largeSlots = Route.LargeSlots)
    {
        SmallSlots = smallSlots;
        LargeSlots = largeSlots;
        if (transferId.Length != 8) throw new ArgumentException("transferId must be 8 bytes", nameof(transferId));
        TransferId = transferId;
        ObjectIdx = objectIdx;
        Key = key;
        KeyBytes = Encoding.UTF8.GetBytes(key);
        if (KeyBytes.Length is 0 or > Layout.MaxKeyBytes)
            throw new ArgumentException($"Object key must be 1..{Layout.MaxKeyBytes} UTF-8 bytes: {key}", nameof(key));
        Layout = Layout.For(size);
    }

    public byte[] TransferId { get; }
    public int ObjectIdx { get; }
    public string Key { get; }
    public byte[] KeyBytes { get; }
    public Layout Layout { get; }
    public int SmallSlots { get; }
    public int LargeSlots { get; }

    public int MaxDatagramBytes => ClientPacket.FixedBytes + KeyBytes.Length + Layout.SymbolBytes;

    /// <summary>Writes a datagram. DATA packets carry the key only when <see cref="ClientPacket.CarriesKey"/> says so, unless <paramref name="forceKey"/>.</summary>
    public int Write(Span<byte> destination, PacketType type, int block, int esi, ReadOnlySpan<byte> symbol, bool forceKey = false)
    {
        bool key = forceKey || type != PacketType.Data || ClientPacket.CarriesKey(esi, Layout.SymbolsIn(Math.Min(block, Layout.Blocks - 1)));
        return ClientPacket.Write(destination, type, SlotOf(block), TransferId, ObjectIdx,
            Layout.Size, block, esi, key ? KeyBytes : [], symbol);
    }

    public byte SlotOf(int block) => Route.Slot(TransferId, ObjectIdx, Layout, block, SmallSlots, LargeSlots);

    /// <summary>The K source symbols of a block, as slices of <paramref name="paddedBlock"/> (length SymbolsIn*SymbolBytes).</summary>
    public ReadOnlyMemory<byte>[] SourceSymbols(int block, ReadOnlyMemory<byte> paddedBlock)
    {
        int k = Layout.SymbolsIn(block), t = Layout.SymbolBytes;
        var symbols = new ReadOnlyMemory<byte>[k];
        for (int i = 0; i < k; i++) symbols[i] = paddedBlock.Slice(i * t, t);
        return symbols;
    }

    /// <summary>Repair symbols for ESI K+first ... K+first+count-1. Source symbols never need encoding.</summary>
    public IEnumerable<(int Esi, Memory<byte> Symbol)> RepairSymbols(int block, ReadOnlyMemory<byte> paddedBlock, int first, int count)
    {
        int k = Layout.SymbolsIn(block);
        var encoder = new RaptorQEncoder(SourceSymbols(block, paddedBlock));
        for (int i = 0; i < count; i++)
        {
            int esi = k + first + i;
            yield return (esi, encoder.GenerateSymbol(esi));
        }
    }
}
