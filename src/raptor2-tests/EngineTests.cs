using System.Security.Cryptography;
using Proxylity.RaptorQ;
using Raptor2.MicroVm;
using Raptor2.Wire;
using Xunit;

namespace Raptor2.Tests;

/// <summary>Drives the MicroVM engine with real encoded packets, through the same JSON batch contract Proxylity uses.</summary>
public sealed class Harness : IDisposable
{
    public readonly string Dir = Path.Combine(Path.GetTempPath(), "raptor2-" + Guid.NewGuid().ToString("N"));
    public readonly FileObjectSink Sink;
    public readonly List<Engine> Vms = [];
    public readonly List<BatchProcessor> Processors = [];
    private long _tag;

    public Harness(int vms = 1, long budget = long.MaxValue)
    {
        Sink = new FileObjectSink(Dir);
        for (int i = 0; i < vms; i++)
        {
            var engine = new Engine(() => Sink, () => "out/", () => budget);
            Vms.Add(engine);
            Processors.Add(new BatchProcessor(engine));
        }
    }

    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }

    /// <summary>One VM per tenant slot, like the real gateway: the datagram's slot byte picks the VM.</summary>
    public async Task<List<HavePacket>> SendAsync(IEnumerable<byte[]> datagrams, int batchSize = 100)
    {
        var haves = new List<HavePacket>();
        foreach (var chunk in datagrams.Chunk(batchSize))
        {
            var bySlot = chunk.GroupBy(d => d[1] % Processors.Count);
            await Task.WhenAll(bySlot.Select(async g =>
            {
                var messages = g.Select(d => new RequestPacket((Interlocked.Increment(ref _tag)).ToString(), null, null, "base64", Convert.ToBase64String(d), null)).ToArray();
                var response = await Processors[g.Key].ProcessAsync(new GatewayRequest(messages));
                lock (haves)
                    foreach (var r in response.Replies)
                        if (HavePacket.TryParse(Convert.FromBase64String(r.Data), out var h)) haves.Add(h!);
            }));
        }
        return haves;
    }

    public string Output(string key) => Path.Combine(Dir, "out", key.Replace('/', Path.DirectorySeparatorChar));
}

public class EngineTests
{
    private static readonly byte[] TransferId = [1, 2, 3, 4, 5, 6, 7, 8];

    private static byte[] Random(long n, int seed)
    {
        var data = new byte[n];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>All datagrams for an object: source symbols, then repair symbols; optionally drops some and shuffles.</summary>
    internal static List<byte[]> Datagrams(ObjectSpec spec, byte[] file, double overhead, double loss, int seed, bool statusAtEnd = false)
    {
        var rng = new Random(seed);
        var list = new List<byte[]>();
        var l = spec.Layout;
        for (int b = 0; b < l.Blocks; b++)
        {
            int k = l.SymbolsIn(b), t = l.SymbolBytes;
            var padded = new byte[k * t];
            long start = (long)b * Layout.BlockBytes;
            Array.Copy(file, start, padded, 0, l.BlockDataBytes(b));
            var all = new List<byte[]>();
            for (int e = 0; e < k; e++)
            {
                var d = new byte[1400];
                int n = spec.Write(d, PacketType.Data, b, e, padded.AsSpan(e * t, t));
                all.Add(d[..n]);
            }
            int repairs = t == 0 ? 0 : Layout.RepairCount(k, overhead);
            foreach (var (esi, symbol) in spec.RepairSymbols(b, padded, 0, repairs))
            {
                var d = new byte[1400];
                int n = spec.Write(d, PacketType.Data, b, esi, symbol.Span);
                all.Add(d[..n]);
            }
            list.AddRange(all.Where(_ => rng.NextDouble() >= loss));
        }
        if (statusAtEnd)
        {
            var d = new byte[1400];
            int n = spec.Write(d, PacketType.Status, 0, 0, []);
            list.Add(d[..n]);
        }
        return list;
    }

    [Fact]
    public void Source_symbols_are_systematic()
    {
        var data = Random(10 * 100, 1);
        var src = Enumerable.Range(0, 10).Select(i => (ReadOnlyMemory<byte>)data.AsMemory(i * 100, 100)).ToArray();
        var enc = new RaptorQEncoder(src);
        for (int i = 0; i < 10; i++) Assert.Equal(data.AsSpan(i * 100, 100).ToArray(), enc.GenerateSymbol(i).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(999)]
    [InlineData(1281)]
    [InlineData(300_000)]
    [InlineData(524_288)]
    [InlineData(524_289)]
    [InlineData(3_000_000)]
    public async Task Single_part_objects_are_written_exactly(int size)
    {
        using var h = new Harness();
        var file = Random(size, size);
        var spec = new ObjectSpec(TransferId, 0, $"dir/file-{size}.bin", size);
        var haves = await h.SendAsync(Datagrams(spec, file, 0.05, 0, 1));
        Assert.Contains(haves, x => x.Complete);
        Assert.Equal(file, await File.ReadAllBytesAsync(h.Output(spec.Key)));
    }

    [Theory]
    [InlineData(1000, 0.05)]
    [InlineData(300_000, 0.05)]
    [InlineData(3_000_000, 0.05)]
    public async Task Loss_within_the_repair_budget_still_decodes(int size, double loss)
    {
        using var h = new Harness();
        var file = Random(size, 7);
        var spec = new ObjectSpec(TransferId, 1, "loss.bin", size);
        // Heavy first pass, then the client's top-up round of fresh repair symbols, as the real client does.
        var first = Datagrams(spec, file, 0.30, loss, 2);
        await h.SendAsync(first);
        if (!File.Exists(h.Output(spec.Key)))
            await h.SendAsync(Datagrams(spec, file, 1.0, 0.5, 3));
        Assert.Equal(file, await File.ReadAllBytesAsync(h.Output(spec.Key)));
        if (size >= 300_000) Assert.True(h.Vms[0].DecodeAttempts > 0, "decoding path was exercised");
    }

    [Fact]
    public async Task Duplicates_and_shuffled_order_are_harmless()
    {
        using var h = new Harness();
        var file = Random(700_000, 9);
        var spec = new ObjectSpec(TransferId, 2, "dup.bin", file.Length);
        var packets = Datagrams(spec, file, 0.1, 0, 4);
        packets.AddRange(packets.Take(500));
        var rng = new Random(5);
        await h.SendAsync(packets.OrderBy(_ => rng.Next()).ToList(), 37);
        Assert.Equal(file, await File.ReadAllBytesAsync(h.Output(spec.Key)));
    }

    [Fact]
    public async Task Multipart_object_spread_over_several_MicroVMs_is_completed_once()
    {
        using var h = new Harness(vms: 8);
        var file = Random(40 * 1024 * 1024 + 123, 11); // 3 segments of 16 MiB
        var spec = new ObjectSpec(TransferId, 3, "big/multi.bin", file.Length);
        Assert.Equal(3, spec.Layout.Segments);
        await h.SendAsync(Datagrams(spec, file, 0.05, 0, 6), 500);

        // Parts are uploaded in the background; poll with STATUS like the client does.
        var status = new byte[1400];
        int n = spec.Write(status, PacketType.Status, 0, 0, []);
        HavePacket? last = null;
        for (int i = 0; i < 100 && last?.Complete != true; i++)
        {
            await Task.Delay(50);
            var haves = await h.SendAsync([status[..n]]);
            last = haves.LastOrDefault();
        }
        Assert.True(last?.Complete, "object should report complete");
        Assert.Equal(spec.Layout.BlocksIn(0), last!.Runs.Sum(r => r.Count)); // STATUS is scoped to the segment it names
        Assert.Equal(file, await File.ReadAllBytesAsync(h.Output(spec.Key)));
        Assert.True(File.Exists(Path.Combine(h.Dir, "out", ".raptor", Convert.ToHexString(TransferId), "3.done")));
    }

    [Fact]
    public async Task A_replacement_MicroVM_recovers_state_from_S3()
    {
        using var h = new Harness(vms: 1);
        var file = Random(20 * 1024 * 1024, 12); // 2 segments
        var spec = new ObjectSpec(TransferId, 4, "resume.bin", file.Length);
        var all = Datagrams(spec, file, 0.05, 0, 8);
        var firstSegmentBlocks = spec.Layout.BlocksIn(0);
        // Deliver only segment 0, let it commit, then "replace" the VM.
        var seg0 = all.Where(d => ClientPacket.TryParse(d, out var p) && p.Block < firstSegmentBlocks).ToList();
        await h.SendAsync(seg0, 500);
        var status = new byte[1400];
        int n = spec.Write(status, PacketType.Status, 0, 0, []);
        for (int i = 0; i < 100 && h.Vms[0].Commits == 0; i++) { await Task.Delay(50); await h.SendAsync([status[..n]]); }

        var replacement = new Engine(() => h.Sink, () => "out/", () => long.MaxValue);
        h.Vms[0] = replacement;
        h.Processors[0] = new BatchProcessor(replacement);
        var haves = await h.SendAsync([status[..n]]);
        var have = haves.Last();
        Assert.False(have.Complete);
        Assert.Equal(firstSegmentBlocks, have.Runs.Sum(r => r.Count)); // committed parts are known from S3

        // The client now sends only what is missing.
        var rest = all.Where(d => ClientPacket.TryParse(d, out var p) && p.Block >= firstSegmentBlocks).ToList();
        await h.SendAsync(rest, 500);
        for (int i = 0; i < 100 && !File.Exists(h.Output(spec.Key)); i++) { await Task.Delay(50); await h.SendAsync([status[..n]]); }
        Assert.Equal(file, await File.ReadAllBytesAsync(h.Output(spec.Key)));
    }

    [Fact]
    public async Task Memory_budget_drops_packets_instead_of_growing()
    {
        using var h = new Harness(budget: 1024);
        var file = Random(100_000, 13);
        var spec = new ObjectSpec(TransferId, 5, "budget.bin", file.Length);
        await h.SendAsync(Datagrams(spec, file, 0.05, 0, 9));
        Assert.False(File.Exists(h.Output(spec.Key)));
        Assert.True(h.Vms[0].Dropped > 0);
        Assert.Equal(0, h.Vms[0].BufferBytes);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/abs")]
    [InlineData(".raptor/x")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    public async Task Dangerous_keys_are_rejected(string key)
    {
        using var h = new Harness();
        var spec = new ObjectSpec(TransferId, 6, key, 10);
        var file = Random(10, 1);
        await h.SendAsync(Datagrams(spec, file, 0.05, 0, 1));
        Assert.Equal(0, h.Vms[0].ObjectCount);
        Assert.False(Directory.Exists(Path.Combine(h.Dir, "out")) && Directory.EnumerateFiles(Path.Combine(h.Dir, "out"), "*", SearchOption.AllDirectories).Any(), "nothing written");
    }

    [Fact]
    public async Task Status_for_an_unknown_object_reports_nothing_held()
    {
        using var h = new Harness();
        var spec = new ObjectSpec(TransferId, 7, "unknown.bin", 5_000_000);
        var d = new byte[1400];
        int n = spec.Write(d, PacketType.Status, 0, 0, []);
        var haves = await h.SendAsync([d[..n]]);
        Assert.False(haves.Single().Complete);
        Assert.Empty(haves.Single().Runs);
    }
}
