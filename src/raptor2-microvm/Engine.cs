using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Proxylity.RaptorQ;
using Raptor2.Wire;

namespace Raptor2.MicroVm;

public enum IngestResult { Dropped, Pending, NewlyHave, AlreadyHave }

internal sealed class BlockState(int k)
{
    public readonly object Gate = new();
    public readonly int K = k;
    public bool Done;
    public bool[]? SourceSeen;
    public int SourceCount;
    public List<(int Esi, byte[] Data)>? Repairs;
    public int NextAttemptAt = k;
}

internal sealed class SegmentState(int blocks)
{
    public readonly object Gate = new();
    public readonly BlockState?[] Blocks = new BlockState?[blocks];
    public byte[]? Buffer;
    public int Have;
    public int CommitState; // 0 idle, 1 committing, 2 committed
}

public sealed class ObjectState
{
    internal ObjectState(string id, string transferHex, int objectIdx, string? key, in Layout layout)
    {
        Id = id; TransferHex = transferHex; ObjectIdx = objectIdx; Layout = layout;
        if (key is not null) TrySetKey(key);
        Segments = new SegmentState?[layout.Segments];
        Committed = new bool[layout.Segments];
        Touch();
    }

    public string Id { get; }
    public string TransferHex { get; }
    public int ObjectIdx { get; }
    // The key arrives in only some packets (protocol v2), so an object can exist before its key is known.
    // Segments are held, not committed, until it is.
    private string? _key;
    private byte[]? _keyBytes;
    public string? Key => Volatile.Read(ref _key);
    internal byte[]? KeyBytes => Volatile.Read(ref _keyBytes);

    /// <summary>Sets the key once. Returns true if it is now set to <paramref name="key"/>, false if a different key was set first.</summary>
    internal bool TrySetKey(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        Interlocked.CompareExchange(ref _keyBytes, bytes, null);
        if (!KeyBytes!.AsSpan().SequenceEqual(bytes)) return false;
        Interlocked.CompareExchange(ref _key, key, null);
        return true;
    }
    public Layout Layout { get; }
    internal SegmentState?[] Segments { get; }
    internal bool[] Committed { get; }
    public volatile bool Complete;
    internal string? UploadId;
    internal readonly SemaphoreSlim UploadGate = new(1, 1);
    internal readonly SemaphoreSlim CompleteGate = new(1, 1);
    internal long RemoteCheckedAt;
    private long _touched;
    public long TouchedTicks => Interlocked.Read(ref _touched);
    public void Touch() => Interlocked.Exchange(ref _touched, Environment.TickCount64);
}

/// <summary>
/// The in-memory state for every object being received, and the work of turning symbols into blocks, blocks into
/// segment buffers and segment buffers into S3 parts. All durable state lives in the sink (S3).
/// </summary>
public sealed class Engine
{
    private readonly ConcurrentDictionary<string, ObjectState> _objects = new();
    private readonly Func<IObjectSink?> _sink;
    private readonly Func<string> _prefix;
    private readonly Func<long> _maxBufferBytes;
    private readonly Action<CommitEvent>? _onCommit;
    private long _bufferBytes, _commits, _decodeAttempts, _dropped;

    public Engine(Func<IObjectSink?> sink, Func<string> prefix, Func<long> maxBufferBytes, Action<CommitEvent>? onCommit = null)
    {
        _sink = sink; _prefix = prefix; _maxBufferBytes = maxBufferBytes; _onCommit = onCommit;
    }

    public int ObjectCount => _objects.Count;
    public long BufferBytes => Interlocked.Read(ref _bufferBytes);
    public long Commits => Interlocked.Read(ref _commits);
    public long DecodeAttempts => Interlocked.Read(ref _decodeAttempts);
    public long Dropped => Interlocked.Read(ref _dropped);
    public void CountDropped() => Interlocked.Increment(ref _dropped);

    // ---- Objects ----

    public ObjectState? GetOrAdd(in ClientPacket p) => GetOrAdd(p, out _);

    /// <summary>
    /// Finds or creates the object a packet belongs to. <paramref name="keyArrived"/> is true when this packet supplied
    /// the key of an object that had none, so segments held for it can now be committed.
    /// </summary>
    public ObjectState? GetOrAdd(in ClientPacket p, out bool keyArrived)
    {
        keyArrived = false;
        var transferHex = Convert.ToHexString(p.TransferId);
        var id = $"{transferHex}/{p.ObjectIdx}";
        string? key = null;
        if (!p.Key.IsEmpty && !TryKey(p.Key, out key)) return null;
        if (!_objects.TryGetValue(id, out var state))
        {
            state = _objects.GetOrAdd(id, new ObjectState(id, transferHex, p.ObjectIdx, key, Layout.For(p.Size)));
            if (state.Layout.Size != p.Size) return null;
            state.Touch();
            if (key is not null) return state.TrySetKey(key) ? state : null;
            return state;
        }
        if (state.Layout.Size != p.Size) return null;
        state.Touch();
        if (key is null) return state;
        bool hadKey = state.Key is not null;
        if (!state.TrySetKey(key)) return null;
        keyArrived = !hadKey;
        return state;
    }

    /// <summary>Keys come from the network: refuse anything that could escape the prefix or collide with our markers.</summary>
    internal static bool TryKey(ReadOnlySpan<byte> utf8, out string key)
    {
        key = "";
        string s;
        try { s = new UTF8Encoding(false, true).GetString(utf8); } catch (ArgumentException) { return false; }
        if (s.Length == 0 || s[0] == '/' || s.StartsWith(".raptor/", StringComparison.Ordinal) || s.Contains('\\')) return false;
        foreach (var c in s) if (char.IsControl(c)) return false;
        foreach (var part in s.Split('/')) if (part is "" or "." or "..") return false;
        key = s;
        return true;
    }

    private string FinalKey(ObjectState o) => _prefix() + (o.Key ?? throw new InvalidOperationException("key not known yet"));
    // Markers live under the same prefix as the objects, so one prefix-scoped IAM grant covers both.
    private string ClaimKey(ObjectState o) => $"{_prefix()}.raptor/{o.TransferHex}/{o.ObjectIdx}.upload";
    private string DoneKey(ObjectState o) => $"{_prefix()}.raptor/{o.TransferHex}/{o.ObjectIdx}.done";

    // ---- Ingest ----

    public IngestResult Ingest(ObjectState o, int block, int esi, ReadOnlySpan<byte> symbol, out bool segmentFull)
    {
        segmentFull = false;
        var layout = o.Layout;
        if ((uint)block >= (uint)layout.Blocks || symbol.Length != layout.SymbolBytes) return IngestResult.Dropped;
        int segIndex = layout.SegmentOf(block);
        if (o.Complete || o.Committed[segIndex]) return IngestResult.AlreadyHave;

        var seg = GetSegment(o, segIndex);
        if (seg is null) return IngestResult.Dropped;
        int local = block - layout.FirstBlockOf(segIndex);
        var b = seg.Blocks[local] ?? Interlocked.CompareExchange(ref seg.Blocks[local], new BlockState(layout.SymbolsIn(block)), null)
                                    ?? seg.Blocks[local]!;
        int k = b.K, t = layout.SymbolBytes;

        lock (b.Gate)
        {
            if (b.Done) return IngestResult.AlreadyHave;
            var buffer = seg.Buffer;
            if (buffer is null) return IngestResult.AlreadyHave; // segment was committed in the meantime
            long offset = layout.OffsetInSegment(block);

            if (esi < k)
            {
                b.SourceSeen ??= new bool[k];
                if (b.SourceSeen[esi]) return IngestResult.Pending;
                b.SourceSeen[esi] = true;
                b.SourceCount++;
                symbol.CopyTo(buffer.AsSpan((int)offset + esi * t, t));
            }
            else
            {
                b.Repairs ??= [];
                foreach (var r in b.Repairs) if (r.Esi == esi) return IngestResult.Pending;
                b.Repairs.Add((esi, symbol.ToArray()));
            }

            int total = b.SourceCount + (b.Repairs?.Count ?? 0);
            if (b.SourceCount == k) b.Done = true;
            else if (total >= k && total >= b.NextAttemptAt)
            {
                if (TryDecode(b, buffer, (int)offset, t)) b.Done = true;
                else b.NextAttemptAt = total < k + 3 ? total + 1 : total + 8;
            }
            if (!b.Done) return IngestResult.Pending;
            b.Repairs = null;
            b.SourceSeen = null;
        }

        segmentFull = Interlocked.Increment(ref seg.Have) == layout.BlocksIn(segIndex);
        return IngestResult.NewlyHave;
    }

    private bool TryDecode(BlockState b, byte[] buffer, int offset, int t)
    {
        Interlocked.Increment(ref _decodeAttempts);
        int k = b.K;
        var symbols = new List<byte[]>(k + 8);
        var esis = new List<int>(k + 8);
        for (int e = 0; e < k; e++)
        {
            if (b.SourceSeen is null || !b.SourceSeen[e]) continue;
            symbols.Add(buffer.AsSpan(offset + e * t, t).ToArray());
            esis.Add(e);
        }
        foreach (var (esi, data) in b.Repairs ?? []) { symbols.Add(data); esis.Add(esi); }
        var decoder = new RaptorQDecoder(k, t);
        if (!decoder.Decode([.. symbols], [.. esis])) return false;
        decoder.ReconstructOriginalObject((long)k * t).CopyTo(buffer, offset);
        return true;
    }

    private SegmentState? GetSegment(ObjectState o, int segIndex)
    {
        var seg = o.Segments[segIndex];
        if (seg is null)
        {
            var created = new SegmentState(o.Layout.BlocksIn(segIndex));
            seg = Interlocked.CompareExchange(ref o.Segments[segIndex], created, null) ?? created;
        }
        if (seg.Buffer is not null) return seg;
        lock (seg.Gate)
        {
            if (seg.Buffer is not null) return seg;
            if (seg.CommitState != 0) return null;
            long length = o.Layout.SegmentBufferBytes(segIndex);
            if (Interlocked.Add(ref _bufferBytes, length) > _maxBufferBytes())
            {
                Interlocked.Add(ref _bufferBytes, -length);
                return null;
            }
            seg.Buffer = GC.AllocateUninitializedArray<byte>((int)length);
            return seg;
        }
    }

    private void FreeBuffer(SegmentState seg)
    {
        lock (seg.Gate)
        {
            if (seg.Buffer is null) return;
            Interlocked.Add(ref _bufferBytes, -seg.Buffer.Length);
            seg.Buffer = null;
        }
    }

    // ---- Commit ----

    /// <summary>Uploads a finished segment. Safe to call repeatedly; only one caller does the work.</summary>
    public async Task CommitSegmentAsync(ObjectState o, int segIndex, CancellationToken ct = default)
    {
        var seg = o.Segments[segIndex];
        var sink = _sink();
        if (seg is null || sink is null || o.Complete || o.Key is null) return; // without a key, hold until one arrives
        if (Interlocked.CompareExchange(ref seg.CommitState, 1, 0) != 0) return;
        var sw = Stopwatch.StartNew();
        string kind = o.Layout.IsSinglePart ? "put" : "part";
        try
        {
            var buffer = seg.Buffer ?? throw new InvalidOperationException("segment has no buffer");
            int length = (int)o.Layout.SegmentDataBytes(segIndex);
            if (o.Layout.IsSinglePart)
            {
                await WithRetry(() => sink.PutObjectAsync(FinalKey(o), buffer, length, ct), ct);
                o.Committed[segIndex] = true;
                o.Complete = true;
            }
            else
            {
                var uploadId = await GetOrCreateUploadAsync(o, sink, ct);
                try
                {
                    await WithRetry(() => sink.UploadPartAsync(FinalKey(o), uploadId, segIndex + 1, buffer, length, ct), ct);
                }
                catch (Exception)
                {
                    // Another MicroVM may already have completed the object, which ends the upload.
                    if (await sink.ReadMarkerAsync(DoneKey(o), ct) is null) throw;
                    o.Complete = true;
                }
                o.Committed[segIndex] = true;
            }
            Volatile.Write(ref seg.CommitState, 2);
            FreeBuffer(seg);
            Interlocked.Increment(ref _commits);
            _onCommit?.Invoke(new("commit", "", o.Id, kind, segIndex + 1, length, sw.ElapsedMilliseconds, null));
            if (!o.Layout.IsSinglePart && !o.Complete && o.Layout.Segments - segIndex <= 64)
                await TryCompleteAsync(o, ct);
        }
        catch (Exception e)
        {
            Volatile.Write(ref seg.CommitState, 0); // a later STATUS retries
            _onCommit?.Invoke(new("commit", "", o.Id, kind, segIndex + 1, 0, sw.ElapsedMilliseconds, e.Message));
        }
    }

    private async Task<string> GetOrCreateUploadAsync(ObjectState o, IObjectSink sink, CancellationToken ct)
    {
        if (o.UploadId is { } known) return known;
        await o.UploadGate.WaitAsync(ct);
        try
        {
            if (o.UploadId is { } again) return again;
            var id = await sink.ReadMarkerAsync(ClaimKey(o), ct);
            if (id is null)
            {
                var mine = await sink.CreateUploadAsync(FinalKey(o), ct);
                if (await sink.TryCreateMarkerAsync(ClaimKey(o), mine, ct)) id = mine;
                else
                {
                    await sink.AbortAsync(FinalKey(o), mine, ct);
                    id = await sink.ReadMarkerAsync(ClaimKey(o), ct) ?? throw new InvalidOperationException("claim vanished");
                }
            }
            o.UploadId = id;
            return id;
        }
        finally { o.UploadGate.Release(); }
    }

    /// <summary>
    /// Looks at S3: records which parts are committed, and if every part is there, completes the upload.
    /// Idempotent and safe to run on several MicroVMs at once.
    /// </summary>
    public async Task TryCompleteAsync(ObjectState o, CancellationToken ct = default)
    {
        var sink = _sink();
        if (sink is null || o.Layout.IsSinglePart || o.Complete || o.Key is null) return;
        await o.CompleteGate.WaitAsync(ct);
        try
        {
            if (o.Complete) return;
            if (await sink.ReadMarkerAsync(DoneKey(o), ct) is not null)
            {
                MarkAllCommitted(o);
                return;
            }
            var uploadId = o.UploadId ?? await sink.ReadMarkerAsync(ClaimKey(o), ct);
            if (uploadId is null) return;
            o.UploadId = uploadId;
            var parts = await sink.ListPartsAsync(FinalKey(o), uploadId, ct);
            int present = 0;
            foreach (var p in parts)
            {
                if (p.PartNumber < 1 || p.PartNumber > o.Layout.Segments) continue;
                if (p.Size != o.Layout.SegmentDataBytes(p.PartNumber - 1)) continue;
                o.Committed[p.PartNumber - 1] = true;
                present++;
            }
            if (present != o.Layout.Segments) return;
            bool ok = await sink.CompleteAsync(FinalKey(o), uploadId, parts, ct);
            if (!ok && await sink.ReadMarkerAsync(DoneKey(o), ct) is null) return; // upload is gone and nobody finished it: leave for the client to resend
            await sink.WriteMarkerAsync(DoneKey(o), "done", ct);
            MarkAllCommitted(o);
            Interlocked.Increment(ref _commits);
            _onCommit?.Invoke(new("commit", "", o.Id, "complete", 0, o.Layout.Size, 0, null));
        }
        finally { o.CompleteGate.Release(); }
    }

    private void MarkAllCommitted(ObjectState o)
    {
        for (int i = 0; i < o.Committed.Length; i++)
        {
            o.Committed[i] = true;
            if (o.Segments[i] is { } s) { Volatile.Write(ref s.CommitState, 2); FreeBuffer(s); }
        }
        o.Complete = true;
    }

    /// <summary>Handles a STATUS query: refresh from S3 (rate limited) and restart any commit that failed.</summary>
    public async Task RefreshAsync(ObjectState o, CancellationToken ct = default)
    {
        if (o.Complete) return;
        long now = Environment.TickCount64;
        if (!o.Layout.IsSinglePart && now - Interlocked.Read(ref o.RemoteCheckedAt) >= 500)
        {
            Interlocked.Exchange(ref o.RemoteCheckedAt, now);
            try { await TryCompleteAsync(o, ct); } catch { /* reported on the next poll */ }
        }
        await CommitPendingAsync(o, ct);
    }

    /// <summary>Commits every full segment that is not committed yet: after a failed commit, or once the key arrives.</summary>
    public async Task CommitPendingAsync(ObjectState o, CancellationToken ct = default)
    {
        for (int i = 0; i < o.Segments.Length; i++)
        {
            var seg = o.Segments[i];
            if (seg is not null && seg.CommitState == 0 && seg.Have == o.Layout.BlocksIn(i) && !o.Committed[i])
                await CommitSegmentAsync(o, i, ct);
        }
    }

    private static async Task WithRetry(Func<Task> action, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { await action(); return; }
            catch (Exception) when (attempt < 4 && !ct.IsCancellationRequested)
            {
                await Task.Delay(100 * attempt * attempt, ct);
            }
        }
    }

    // ---- Reporting ----

    /// <summary>Every block this MicroVM holds or has committed, as runs of block numbers.</summary>
    public List<(int Start, int Count)> HeldRuns(ObjectState o, int scopeStart = 0, int scopeCount = int.MaxValue)
    {
        var layout = o.Layout;
        int scopeEnd = (int)Math.Min((long)scopeStart + scopeCount, layout.Blocks);
        if (o.Complete) return [(scopeStart, scopeEnd - scopeStart)];
        var runs = new List<(int Start, int Count)>();
        void Add(int start, int count)
        {
            int end = Math.Min(start + count, scopeEnd);
            start = Math.Max(start, scopeStart);
            count = end - start;
            if (count <= 0) return;
            if (runs.Count > 0 && runs[^1].Start + runs[^1].Count == start) runs[^1] = (runs[^1].Start, runs[^1].Count + count);
            else runs.Add((start, count));
        }
        for (int i = 0; i < layout.Segments; i++)
        {
            int first = layout.FirstBlockOf(i), count = layout.BlocksIn(i);
            if (o.Committed[i]) { Add(first, count); continue; }
            var seg = o.Segments[i];
            if (seg is null) continue;
            for (int j = 0; j < count; j++)
                if (seg.Blocks[j] is { Done: true }) Add(first + j, 1);
        }
        return runs;
    }

    /// <summary>
    /// For each incomplete block in scope, how many more distinct symbols this MicroVM needs: K minus what it holds
    /// (at least 1, since decoding can still fail with K), or K when it holds nothing for the block.
    /// </summary>
    public List<(int Block, int Need)> Needs(ObjectState o, int scopeStart, int scopeCount, int max = Raptor2.Wire.HavePacket.MaxNeeds)
    {
        var needs = new List<(int, int)>();
        var layout = o.Layout;
        if (o.Complete) return needs;
        int end = (int)Math.Min((long)scopeStart + scopeCount, layout.Blocks);
        for (int block = Math.Max(0, scopeStart); block < end && needs.Count < max; block++)
        {
            int segIndex = layout.SegmentOf(block);
            if (o.Committed[segIndex]) { block = layout.FirstBlockOf(segIndex) + layout.BlocksIn(segIndex) - 1; continue; }
            var b = o.Segments[segIndex]?.Blocks[block - layout.FirstBlockOf(segIndex)];
            if (b is null) { needs.Add((block, layout.SymbolsIn(block))); continue; }
            lock (b.Gate)
            {
                if (b.Done) continue;
                int have = b.SourceCount + (b.Repairs?.Count ?? 0);
                needs.Add((block, Math.Max(1, b.K - have)));
            }
        }
        return needs;
    }

    // ---- Housekeeping ----

    /// <summary>Drops finished objects after a short grace period and abandoned ones after a long one. Returns how many.</summary>
    public int Sweep(TimeSpan completeAfter, TimeSpan abandonedAfter)
    {
        int removed = 0;
        long now = Environment.TickCount64;
        foreach (var (id, o) in _objects)
        {
            var idle = TimeSpan.FromMilliseconds(now - o.TouchedTicks);
            if (idle < (o.Complete ? completeAfter : abandonedAfter)) continue;
            if (!_objects.TryRemove(id, out _)) continue;
            foreach (var s in o.Segments) if (s is not null) FreeBuffer(s);
            removed++;
        }
        return removed;
    }
}
