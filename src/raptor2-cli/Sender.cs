using System.Diagnostics;
using System.Threading.Channels;
using Raptor2.Wire;

namespace Raptor2.Cli;

public sealed class SendOptions
{
    /// <summary>Fixed repair overhead (fraction of K), or null to adapt it to the measured loss.</summary>
    public double? Overhead { get; init; }
    public double RateMbps { get; init; } = 10;
    public int Lanes { get; init; } // 0 = from rate
    public int SmallSlots { get; init; } = Route.SmallSlots;
    public int LargeSlots { get; init; } = Route.LargeSlots;
    public TimeSpan MaxBlockTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan NoProgressTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxRounds { get; init; } = 12;
    public bool WaitForCompletion { get; init; } = true;
    public Action<string>? Log { get; init; }
    public Action<string>? Detail { get; init; }
}

public sealed class SegmentTx
{
    public long SentAt;      // ms (Stopwatch) when the last pass over this segment reached the socket
    public long StatusAt;    // ms when a STATUS was last sent for it
    public int Round;
    public int HaveCount;
    public volatile bool Queued;
    public volatile bool EverSent;
    // First-pass record for the repair controller.
    public int FirstPassRepairs, FirstPassK;
    public bool Reported;
}

public sealed class ObjectTx
{
    public ObjectTx(ObjectSpec spec, string path)
    {
        Spec = spec; Path = path;
        Have = new bool[spec.Layout.Blocks];
        NextRepair = new int[spec.Layout.Blocks];
        Need = new int[spec.Layout.Blocks];
        Array.Fill(Need, -1);
        Segments = Enumerable.Range(0, spec.Layout.Segments).Select(_ => new SegmentTx()).ToArray();
    }

    public ObjectSpec Spec { get; }
    public string Path { get; }
    public bool[] Have { get; }
    public int[] NextRepair { get; }
    /// <summary>Symbols the MicroVM said a block still needs (from the last STATUS reply), or -1 if unknown.</summary>
    public int[] Need { get; }
    public SegmentTx[] Segments { get; }
    public volatile bool Complete;
    public int HaveCount;
    public long LastPollAt;
}

public sealed class SendStats
{
    public long Packets, Bytes, Retries, Replies, RepairSymbols;
}

/// <summary>
/// Sends objects as fire-and-forget blocks (K source symbols, then any up-front repair symbols) and then repairs only
/// what the MicroVMs report missing. There is no handshake: every datagram says what it belongs to.
///
/// Block workers read and encode; one <see cref="WireSender"/> thread paces and sends.
/// </summary>
public sealed class Sender
{
    private readonly ITransport _transport;
    private readonly SendOptions _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Channel<(ObjectTx Object, int Segment)> _work = Channel.CreateUnbounded<(ObjectTx, int)>();
    private readonly Dictionary<int, ObjectTx> _byIndex = new();
    private readonly List<ObjectTx> _objects = [];
    private readonly byte[] _transferId;
    private readonly RepairController _repairs;
    private WireSender? _wire;
    private long _lastProgress;
    private int _unfinished, _active;

    public SendStats Stats { get; } = new();
    public RepairController Repairs => _repairs;

    public Sender(ITransport transport, SendOptions options, byte[] transferId)
    {
        _transport = transport; _options = options; _transferId = transferId;
        _repairs = new RepairController(options.Overhead);
    }

    public ObjectSpec AddObject(string key, string path, long size)
    {
        int idx = _objects.Count;
        var spec = new ObjectSpec(_transferId, idx, key, size, _options.SmallSlots, _options.LargeSlots);
        var o = new ObjectTx(spec, path);
        _objects.Add(o);
        _byIndex[idx] = o;
        return spec;
    }

    public IReadOnlyList<ObjectTx> Objects => _objects;
    private long Now => _clock.ElapsedMilliseconds;

    public async Task<bool> RunAsync(CancellationToken ct)
    {
        _unfinished = _objects.Count;
        _lastProgress = Now;
        int lanes = _options.Lanes > 0 ? _options.Lanes : Layout.LanesForRate(_options.RateMbps);
        int workers = Math.Max(2, Math.Min(lanes, _options.LargeSlots));
        _options.Detail?.Invoke($"{_objects.Count} object(s), {workers} block worker(s), {_options.RateMbps} Mbps, " +
            (_repairs.Adaptive ? "adaptive repair" : $"fixed overhead {_options.Overhead}"));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var wire = new WireSender(_transport, _options.RateMbps, queuedBatches: Math.Max(4, workers * 2));
        _wire = wire;
        var receive = Task.Run(() => ReceiveLoopAsync(cts.Token), CancellationToken.None);
        var senders = Enumerable.Range(0, workers).Select(_ => Task.Run(() => WorkerAsync(cts.Token), CancellationToken.None)).ToArray();

        // Feed segments in file order. Consecutive segments go to different workers, so a large object is
        // sent over several slots at once without the client ever knowing what a part is.
        foreach (var o in _objects)
            for (int s = 0; s < o.Spec.Layout.Segments; s++)
            {
                while (_work.Reader.Count > workers * 4) await Task.Delay(1, cts.Token);
                o.Segments[s].Queued = true;
                _work.Writer.TryWrite((o, s));
            }

        bool ok = true;
        if (_options.WaitForCompletion) ok = await MonitorAsync(cts.Token);
        else while (_work.Reader.Count > 0 || Volatile.Read(ref _active) > 0) await Task.Delay(5, cts.Token);

        _work.Writer.TryComplete();
        cts.Cancel();
        try { await Task.WhenAll(senders.Append(receive)); } catch (OperationCanceledException) { }
        wire.Complete();
        Stats.Packets = wire.Packets;
        Stats.Bytes = wire.Bytes;
        return ok;
    }

    // ---- Producing ----

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (o, segment) in _work.Reader.ReadAllAsync(ct))
            {
                Interlocked.Increment(ref _active);
                try { await SendSegmentAsync(o, segment, ct); }
                finally { Interlocked.Decrement(ref _active); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendSegmentAsync(ObjectTx o, int segment, CancellationToken ct)
    {
        var seg = o.Segments[segment];
        int round = seg.Round;
        var layout = o.Spec.Layout;
        int first = layout.FirstBlockOf(segment), count = layout.BlocksIn(segment);
        if (round == 0)
        {
            seg.FirstPassK = layout.SymbolsIn(first);
            seg.FirstPassRepairs = layout.SymbolBytes == 0 ? 0 : _repairs.RepairsFor(seg.FirstPassK, tail: segment == layout.Segments - 1);
        }
        DatagramBatch? last = null;
        using (var handle = layout.Size > 0 ? File.OpenHandle(o.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null)
        {
            for (int b = first; b < first + count && !o.Complete; b++)
            {
                if (o.Have[b]) continue;
                last = BuildBlock(o, seg, handle, b, round);
                last.Sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await _wire!.EnqueueAsync(last, ct);
            }
        }
        // Time the repair timeout from when the segment's last datagram actually left, not from when it was queued.
        if (last?.Sent is { } sent) await sent.Task.WaitAsync(ct);
        seg.EverSent = true;
        seg.SentAt = Now;
        seg.Queued = false;
        Interlocked.Exchange(ref _lastProgress, Now);
    }

    /// <summary>Reads a block and lays out its datagrams: K source symbols, then repair symbols (new ESIs every round).</summary>
    private DatagramBatch BuildBlock(ObjectTx o, SegmentTx seg, Microsoft.Win32.SafeHandles.SafeFileHandle? handle, int block, int round)
    {
        var spec = o.Spec;
        var layout = spec.Layout;
        int k = layout.SymbolsIn(block), t = layout.SymbolBytes;
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(1, k * t));
        try
        {
        var padded = rented.AsSpan(0, Math.Max(1, k * t));
        padded.Clear();
        int dataBytes = layout.BlockDataBytes(block);
        int read = 0;
        while (read < dataBytes)
        {
            int n = RandomAccess.Read(handle!, padded.Slice(read, dataBytes - read), (long)block * Layout.BlockBytes + read);
            if (n <= 0) throw new IOException($"File changed while sending: {o.Path}");
            read += n;
        }

        // Round 0: the controller's count. Later rounds: fresh repair symbols, and from round 2 the source symbols
        // again in case a MicroVM lost what it had. Repair rounds always carry the key, so a replacement MicroVM
        // learns it.
        // A repair round sends what the MicroVM said the block still needs, plus a margin for loss on the way. When
        // it holds nothing for the block (it may have been replaced), or said nothing, everything is resent.
        int need = Interlocked.Exchange(ref o.Need[block], -1);
        bool sendSources;
        int repairCount;
        if (t == 0) { sendSources = true; repairCount = 0; }
        else if (round == 0) { sendSources = true; repairCount = k == seg.FirstPassK ? seg.FirstPassRepairs : _repairs.RepairsFor(k, tail: layout.SegmentOf(block) == layout.Segments - 1); }
        else if (need >= 0 && need < k)
        {
            sendSources = false;
            double margin = Math.Max(0.25, 3 * _repairs.LossEstimate);
            repairCount = need + Math.Max(2, (int)Math.Ceiling(need * margin));
        }
        else { sendSources = round >= 2 || need >= k; repairCount = Math.Max(4, (int)Math.Ceiling(k * 0.25)); }
        int firstRepair = o.NextRepair[block];
        if (firstRepair + repairCount + k > 65_000) firstRepair = 0;
        o.NextRepair[block] = firstRepair + repairCount;
        bool forceKey = round > 0;

        int perDatagram = ClientPacket.FixedBytes + spec.KeyBytes.Length + t;
        var batch = new DatagramBatch(perDatagram * ((sendSources ? k : 0) + repairCount) + perDatagram, () => o.Complete);
        if (sendSources)
            for (int e = 0; e < k; e++)
                batch.Commit(spec.Write(batch.Next, PacketType.Data, block, e, padded.Slice(e * t, t), forceKey));
        if (repairCount > 0)
        {
            foreach (var (esi, symbol) in spec.RepairSymbols(block, rented.AsMemory(0, k * t), firstRepair, repairCount))
                batch.Commit(spec.Write(batch.Next, PacketType.Data, block, esi, symbol.Span, forceKey));
            Interlocked.Add(ref Stats.RepairSymbols, repairCount);
        }
        if (round > 0) Interlocked.Increment(ref Stats.Retries);
        return batch;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(rented); }
    }

    private void SendStatus(ObjectTx o, int segment)
    {
        Span<byte> datagram = stackalloc byte[ClientPacket.MaxDatagram];
        int n = o.Spec.Write(datagram, PacketType.Status, o.Spec.Layout.FirstBlockOf(segment), 0, []);
        _wire!.SendNow(datagram[..n]);
    }

    // ---- Receiving ----

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[2048];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _transport.ReceiveAsync(buffer, ct);
                if (!HavePacket.TryParse(buffer.AsSpan(0, n), out var have) || have is null) continue;
                if (!have.TransferId.AsSpan().SequenceEqual(_transferId) || !_byIndex.TryGetValue(have.ObjectIdx, out var o)) continue;
                Interlocked.Increment(ref Stats.Replies);
                Apply(o, have);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void Apply(ObjectTx o, HavePacket have)
    {
        var layout = o.Spec.Layout;
        if (have.ScopeCount > 0 && !have.Complete)
        {
            // A snapshot: anything in scope that is not listed is not held (for example after a MicroVM was replaced).
            var listed = new HashSet<int>();
            foreach (var (s, c) in have.Runs) for (int b = s; b < s + c; b++) listed.Add(b);
            int end = Math.Min(layout.Blocks, have.ScopeStart + have.ScopeCount);
            for (int b = have.ScopeStart; b < end; b++)
                if (o.Have[b] && !listed.Contains(b))
                {
                    o.Have[b] = false;
                    Interlocked.Decrement(ref o.HaveCount);
                    Interlocked.Decrement(ref o.Segments[layout.SegmentOf(b)].HaveCount);
                }
        }
        foreach (var (block, n) in have.Needs)
            if ((uint)block < (uint)layout.Blocks && !o.Have[block]) Volatile.Write(ref o.Need[block], n);
        foreach (var (start, count) in have.Runs)
        {
            int end = Math.Min(layout.Blocks, start + count);
            for (int b = Math.Max(0, start); b < end; b++)
            {
                if (o.Have[b]) continue;
                o.Have[b] = true;
                Interlocked.Increment(ref o.HaveCount);
                Interlocked.Increment(ref o.Segments[layout.SegmentOf(b)].HaveCount);
                Interlocked.Exchange(ref _lastProgress, Now);
            }
        }
        if (have.Complete && !o.Complete)
        {
            o.Complete = true;
            Interlocked.Decrement(ref _unfinished);
            Interlocked.Exchange(ref _lastProgress, Now);
            _options.Detail?.Invoke($"complete: {o.Spec.Key}");
        }
    }

    // ---- Repair loop ----

    /// <summary>Tells the repair controller how a segment's first pass went (once per segment).</summary>
    private void ReportFirstPass(ObjectTx o, int s, int missing)
    {
        var seg = o.Segments[s];
        if (seg.Reported || seg.Round != 0 || !seg.EverSent) return;
        seg.Reported = true;
        _repairs.Report(o.Spec.Layout.BlocksIn(s), missing, seg.FirstPassRepairs, seg.FirstPassK);
    }

    private async Task<bool> MonitorAsync(CancellationToken ct)
    {
        var active = new List<ObjectTx>(_objects);
        while (Volatile.Read(ref _unfinished) > 0)
        {
            await Task.Delay(25, ct);
            long now = Now;
            if (now - Interlocked.Read(ref _lastProgress) > _options.NoProgressTimeout.TotalMilliseconds)
            {
                _options.Log?.Invoke($"No progress for {_options.NoProgressTimeout.TotalSeconds:F0}s; giving up on {_unfinished} object(s).");
                return false;
            }
            active.RemoveAll(o =>
            {
                if (!o.Complete) return false;
                for (int s = 0; s < o.Segments.Length; s++) ReportFirstPass(o, s, 0);
                return true;
            });
            foreach (var o in active)
            {
                var layout = o.Spec.Layout;
                bool allSent = true;
                bool askedStatus = false;
                for (int s = 0; s < o.Segments.Length; s++)
                {
                    var seg = o.Segments[s];
                    if (!seg.EverSent || seg.Queued) { allSent = false; continue; }
                    if (seg.HaveCount >= layout.BlocksIn(s)) { ReportFirstPass(o, s, 0); continue; }
                    long timeout = Math.Min((long)_options.MaxBlockTimeout.TotalMilliseconds, 250L << Math.Min(seg.Round, 4));
                    if (now - seg.SentAt < timeout) continue;
                    if (seg.StatusAt < seg.SentAt)
                    {
                        if (askedStatus) continue; // one STATUS per object per pass keeps replies unambiguous
                        askedStatus = true;
                        seg.StatusAt = now;
                        SendStatus(o, s); // the reply says what the MicroVM really holds
                        continue;
                    }
                    if (now - seg.StatusAt < 200) continue;
                    if (seg.Round >= _options.MaxRounds) continue;
                    ReportFirstPass(o, s, layout.BlocksIn(s) - seg.HaveCount);
                    seg.Round++;
                    seg.Queued = true;
                    _options.Detail?.Invoke($"repair round {seg.Round}: {o.Spec.Key} segment {s} ({layout.BlocksIn(s) - seg.HaveCount} block(s) missing, " +
                        $"loss estimate {_repairs.LossEstimate:P3})");
                    _work.Writer.TryWrite((o, s));
                }
                // Everything is held (or committed) but the object is not complete: ask again until S3 says it is.
                if (allSent && o.HaveCount >= layout.Blocks && now - o.LastPollAt >= 100)
                {
                    o.LastPollAt = now;
                    SendStatus(o, layout.Segments - 1);
                }
            }
        }
        return true;
    }
}
