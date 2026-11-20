using Raptor2.Wire;
using Xunit;

namespace Raptor2.Tests;

public class LayoutTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(1000L)]
    [InlineData(1280L)]
    [InlineData(1281L)]
    [InlineData(524_288L)]
    [InlineData(524_289L)]
    [InlineData(10_000_000L)]
    [InlineData(100L * 1024 * 1024)]
    [InlineData(1L << 40)]
    [InlineData(5L << 40)]
    public void Segments_cover_the_object_exactly(long size)
    {
        var l = Layout.For(size);
        long total = 0;
        for (int s = 0; s < l.Segments; s++)
        {
            total += l.SegmentDataBytes(s);
            if (s < l.Segments - 1) Assert.True(l.SegmentDataBytes(s) >= 5 * 1024 * 1024, "non-final parts must be >= 5 MiB");
            Assert.True(l.SegmentBufferBytes(s) >= l.SegmentDataBytes(s));
        }
        Assert.Equal(size, total);
        Assert.InRange(l.Segments, 1, 10_000);
        long data = 0;
        for (int b = 0; b < l.Blocks; b += Math.Max(1, l.Blocks / 1000)) Assert.True(l.SymbolsIn(b) * l.SymbolBytes >= l.BlockDataBytes(b));
        Assert.True(l.SymbolBytes <= Layout.MaxSymbolBytes);
        _ = data;
    }

    [Fact]
    public void Small_objects_are_one_segment_one_block()
    {
        var l = Layout.For(1000);
        Assert.True(l.IsSinglePart);
        Assert.Equal(1, l.Blocks);
        Assert.Equal(1, l.SymbolsIn(0));
        Assert.Equal(1000, l.SymbolBytes);
    }

    [Fact]
    public void Large_object_uses_16MiB_segments_until_part_limit()
    {
        Assert.Equal(5, Layout.For(1L << 30).SegmentShift);   // 1 GiB: 64 parts
        Assert.Equal(8, Layout.For(1L << 40).SegmentShift);   // 1 TiB: 8192 parts of 128 MiB
        Assert.True(Layout.For(5L << 40).Segments <= Layout.MaxParts);
    }

    [Fact]
    public void Slots_stay_in_range_and_spread_consecutive_segments()
    {
        var id = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var l = Layout.For(2L << 30);
        var slots = new HashSet<byte>();
        for (int s = 0; s < 60; s++)
        {
            var slot = Route.Slot(id, 0, l, s << l.SegmentShift);
            Assert.InRange(slot, Route.SmallSlots, Route.Slots - 1);
            slots.Add(slot);
        }
        Assert.Equal(60, slots.Count);
        Assert.InRange(Route.Slot(id, 7, Layout.For(100), 0), 0, Route.SmallSlots - 1);
        // A smaller pool: 2 small + 6 large.
        var used = Enumerable.Range(0, 60).Select(s => Route.Slot(id, 0, l, s << l.SegmentShift, 2, 6)).ToHashSet();
        Assert.Equal(Enumerable.Range(2, 6).Select(i => (byte)i).ToHashSet(), used);
        Assert.InRange(Route.Slot(id, 3, Layout.For(100), 0, 2, 6), 0, 1);
    }
}

public class PacketTests
{
    [Fact]
    public void Data_packet_round_trips()
    {
        var spec = new ObjectSpec([9, 8, 7, 6, 5, 4, 3, 2], 70_000, "2026/a b/ü.bin", 123_456_789);
        var symbol = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
        var datagram = new byte[1400];
        int n = spec.Write(datagram, PacketType.Data, 12345, 513, symbol);
        Assert.True(ClientPacket.TryParse(datagram.AsSpan(0, n), out var p));
        Assert.Equal(PacketType.Data, p.Type);
        Assert.Equal(70_000, p.ObjectIdx);
        Assert.Equal(123_456_789, p.Size);
        Assert.Equal(12345, p.Block);
        Assert.Equal(513, p.Esi);
        Assert.True(p.Key.SequenceEqual(spec.KeyBytes));
        Assert.True(p.Symbol.SequenceEqual(symbol));
        Assert.Equal(Route.Slot(spec.TransferId, spec.ObjectIdx, spec.Layout, 12345), p.Slot);
    }

    [Fact]
    public void Have_packet_round_trips()
    {
        var have = new HavePacket { TransferId = [1, 2, 3, 4, 5, 6, 7, 8], ObjectIdx = 5, Complete = true, Runs = [(0, 10), (4000000, 3)] };
        Assert.True(HavePacket.TryParse(have.ToBytes(), out var back));
        Assert.Equal(have.TransferId, back!.TransferId);
        Assert.Equal(5, back.ObjectIdx);
        Assert.True(back.Complete);
        Assert.Equal(have.Runs, back.Runs);
    }

    [Fact]
    public void Runs_collapse_adjacent_blocks()
    {
        Assert.Equal([(1, 3), (7, 1), (9, 2)], HavePacket.ToRuns([1, 2, 3, 7, 9, 10]));
    }

    [Fact]
    public void Rejects_garbage()
    {
        Assert.False(ClientPacket.TryParse(new byte[10], out _));
        Assert.False(ClientPacket.TryParse(new byte[40], out _)); // wrong type/version
    }
}
