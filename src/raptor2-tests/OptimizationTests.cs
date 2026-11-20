using Raptor2.Cli;
using Raptor2.MicroVm;
using Raptor2.Wire;
using Xunit;

namespace Raptor2.Tests;

public class KeySubsetTests
{
    private static readonly byte[] TransferId = [8, 7, 6, 5, 4, 3, 2, 1];

    /// <summary>The same datagram without its key.</summary>
    private static byte[] StripKey(byte[] d)
    {
        int keyLen = d[24];
        var stripped = new byte[d.Length - keyLen];
        d.AsSpan(0, ClientPacket.FixedBytes).CopyTo(stripped);
        stripped[24] = 0;
        d.AsSpan(ClientPacket.FixedBytes + keyLen).CopyTo(stripped.AsSpan(ClientPacket.FixedBytes));
        return stripped;
    }

    [Fact]
    public void Only_some_data_packets_carry_the_key()
    {
        Assert.True(ClientPacket.CarriesKey(0, 512));
        Assert.False(ClientPacket.CarriesKey(1, 512));
        Assert.True(ClientPacket.CarriesKey(16, 512));
        Assert.True(ClientPacket.CarriesKey(512, 512));   // repair symbols
        Assert.True(ClientPacket.CarriesKey(5, 10));      // small blocks: every packet
        var spec = new ObjectSpec(TransferId, 0, "some/long/key/name.bin", 10_000_000);
        var d = new byte[1400];
        int withKey = spec.Write(d, PacketType.Data, 3, 0, new byte[1024]);
        int without = spec.Write(d, PacketType.Data, 3, 1, new byte[1024]);
        Assert.Equal(spec.KeyBytes.Length, withKey - without);
        Assert.True(ClientPacket.TryParse(d.AsSpan(0, without), out var p));
        Assert.True(p.Key.IsEmpty);
        Assert.Equal(1024, p.Symbol.Length);
    }

    [Fact]
    public async Task A_segment_that_fills_before_its_key_arrives_is_committed_when_it_does()
    {
        using var h = new Harness();
        var file = new byte[3_000_000];
        new Random(3).NextBytes(file);
        var spec = new ObjectSpec(TransferId, 1, "late/key.bin", file.Length);
        var all = EngineTests.Datagrams(spec, file, 0.05, 0, 1);
        await h.SendAsync(all.Select(StripKey).ToList());
        Assert.False(File.Exists(h.Output(spec.Key)));                 // all symbols, but no key yet
        Assert.Equal(1, h.Vms[0].ObjectCount);

        var status = new byte[1400];
        int n = spec.Write(status, PacketType.Status, 0, 0, []);
        var haves = await h.SendAsync([status[..n]]);                  // STATUS always carries the key
        Assert.True(haves.Single().Complete);
        Assert.Equal(file, await File.ReadAllBytesAsync(h.Output(spec.Key)));
    }

    [Fact]
    public async Task A_keyed_data_packet_also_releases_a_held_segment()
    {
        using var h = new Harness();
        var file = new byte[2_000_000];
        new Random(4).NextBytes(file);
        var spec = new ObjectSpec(TransferId, 2, "late/key2.bin", file.Length);
        var all = EngineTests.Datagrams(spec, file, 0.05, 0, 1);
        await h.SendAsync(all.Select(StripKey).ToList());
        Assert.False(File.Exists(h.Output(spec.Key)));
        var haves = await h.SendAsync([all.First(d => d[24] != 0)]);
        Assert.Contains(haves, x => x.Complete);
        Assert.Equal(file, await File.ReadAllBytesAsync(h.Output(spec.Key)));
    }

    [Fact]
    public async Task A_different_key_for_the_same_object_is_rejected()
    {
        using var h = new Harness();
        var a = new ObjectSpec(TransferId, 3, "a.bin", 100);
        var b = new ObjectSpec(TransferId, 3, "b.bin", 100);
        var d = new byte[1400];
        await h.SendAsync([d[..a.Write(d, PacketType.Status, 0, 0, [])]]);
        long before = h.Vms[0].Dropped;
        var e = new byte[1400];
        await h.SendAsync([e[..b.Write(e, PacketType.Status, 0, 0, [])]]);
        Assert.Equal(before + 1, h.Vms[0].Dropped);
    }

    [Fact]
    public async Task Status_reports_what_each_incomplete_block_still_needs()
    {
        using var h = new Harness();
        var file = new byte[1_500_000];
        new Random(5).NextBytes(file);
        var spec = new ObjectSpec(TransferId, 4, "needs.bin", file.Length);
        var all = EngineTests.Datagrams(spec, file, 0, 0, 1); // sources only
        // Drop 7 source symbols of block 1.
        var dropped = all.Where(d => ClientPacket.TryParse(d, out var p) && p.Block == 1 && p.Esi is >= 100 and < 107).ToHashSet();
        await h.SendAsync(all.Where(d => !dropped.Contains(d)).ToList());
        var status = new byte[1400];
        int n = spec.Write(status, PacketType.Status, 0, 0, []);
        var have = (await h.SendAsync([status[..n]])).Single();
        Assert.Equal([(1, 6)], have.Needs); // 7 lost, 1 repair symbol (ESI K) was sent up front

        // Exactly 6 more repair symbols for block 1 complete the object.
        var padded = new byte[spec.Layout.SymbolsIn(1) * spec.Layout.SymbolBytes];
        Array.Copy(file, Layout.BlockBytes, padded, 0, spec.Layout.BlockDataBytes(1));
        var repairs = spec.RepairSymbols(1, padded, 1, 6).Select(r =>
        {
            var d = new byte[1400];
            return d[..spec.Write(d, PacketType.Data, 1, r.Esi, r.Symbol.Span)];
        }).ToList();
        var haves = await h.SendAsync(repairs);
        Assert.Contains(haves, x => x.Complete);
        Assert.Equal(file, await File.ReadAllBytesAsync(h.Output(spec.Key)));
    }

    [Fact]
    public void Have_needs_round_trip()
    {
        var have = new HavePacket { ObjectIdx = 1, ScopeCount = 32, Runs = [(0, 3)], Needs = [(3, 5), (7, 512)] };
        Assert.True(HavePacket.TryParse(have.ToBytes(), out var back));
        Assert.Equal(have.Needs, back!.Needs);
        Assert.Equal(have.Runs, back.Runs);
    }

    [Fact]
    public void Version_1_packets_are_rejected()
    {
        var spec = new ObjectSpec(TransferId, 0, "k", 10);
        var d = new byte[1400];
        int n = spec.Write(d, PacketType.Data, 0, 0, new byte[10]);
        d[0] = (byte)((d[0] & 0xF0) | 1);
        Assert.False(ClientPacket.TryParse(d.AsSpan(0, n), out _));
    }
}

public class RepairControllerTests
{
    [Fact]
    public void Fixed_mode_matches_the_original_overhead_rule()
    {
        var c = new RepairController(0.05);
        Assert.False(c.Adaptive);
        Assert.Equal(26, c.RepairsFor(512));
        Assert.Equal(1, c.RepairsFor(1));
        c.Report(32, 32, 26, 512); // ignored
        Assert.Equal(26, c.RepairsFor(512));
    }

    [Fact]
    public void Starts_low_and_backs_off_to_zero_on_a_clean_link()
    {
        var c = new RepairController(null);
        int start = c.RepairsFor(512);
        Assert.InRange(start, 1, 8);
        for (int i = 0; i < 12; i++) c.Report(32, 0, c.RepairsFor(512), 512);
        Assert.Equal(0, c.RepairsFor(512));
        Assert.True(c.RepairsFor(1) >= 1, "small blocks always get a repair symbol");
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.03)]
    [InlineData(0.10)]
    public void Converges_near_the_real_loss_rate(double loss)
    {
        var c = new RepairController(null);
        var rng = new Random(7);
        int missesLast = 0, blocksLast = 0;
        for (int segment = 0; segment < 40; segment++)
        {
            int r = c.RepairsFor(512), missing = 0;
            for (int b = 0; b < 32; b++)
            {
                int lost = 0;
                for (int i = 0; i < 512 + r; i++) if (rng.NextDouble() < loss) lost++;
                if (lost > r) missing++;
            }
            c.Report(32, missing, r, 512);
            if (segment >= 30) { missesLast += missing; blocksLast += 32; }
        }
        Assert.InRange(c.LossEstimate, loss * 0.5, loss * 2);
        Assert.True(missesLast / (double)blocksLast < 0.12, $"{missesLast}/{blocksLast} blocks needed repair at the end");
    }

    [Fact]
    public void Reacts_within_a_few_segments_when_loss_appears()
    {
        var c = new RepairController(null);
        for (int i = 0; i < 12; i++) c.Report(32, 0, c.RepairsFor(512), 512);
        Assert.Equal(0, c.RepairsFor(512));
        for (int i = 0; i < 3; i++) c.Report(32, 32, c.RepairsFor(512), 512); // heavy loss arrives: every block misses
        Assert.True(c.RepairsFor(512) >= 15, $"got {c.RepairsFor(512)}");
    }

    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(3, 2)]
    [InlineData(10, 15)]
    [InlineData(40, 20)]
    public void Log_tails_match_the_direct_sums(double lambda, int r)
    {
        var (logTail, logCdf) = RepairController.LogTails(lambda, r);
        double tail = RepairController.TailAbove(lambda, r);
        Assert.Equal(Math.Log(tail), logTail, 6);
        Assert.Equal(Math.Log(1 - tail), logCdf, 6);
        var (farTail, _) = RepairController.LogTails(0.01, 30); // 1 - cdf would cancel to 0 here
        Assert.True(double.IsFinite(farTail) && farTail < -150);
    }

    [Fact]
    public void Tail_segments_get_more_protection()
    {
        var c = new RepairController(null);
        for (int i = 0; i < 5; i++) c.Report(32, 3, c.RepairsFor(512), 512);
        Assert.True(c.RepairsFor(512, tail: true) > c.RepairsFor(512));
    }

    [Fact]
    public void Poisson_helpers()
    {
        Assert.Equal(0, RepairController.TailAbove(0, 0));
        Assert.InRange(RepairController.TailAbove(1, 0), 0.632, 0.633);
        Assert.Equal(0, RepairController.Quantile(0.001, 0.01));
        Assert.InRange(RepairController.Quantile(15, 0.01), 24, 26);
    }
}
