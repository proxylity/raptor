using Raptor2.Wire;

namespace Raptor2.MicroVm;

/// <summary>Turns one Proxylity batch into replies. Many objects can share a batch and one object spans many batches.</summary>
public sealed class BatchProcessor(Engine engine)
{
    private long _batches, _packets;
    public long Batches => Interlocked.Read(ref _batches);
    public long Packets => Interlocked.Read(ref _packets);

    private sealed class Touched
    {
        public ObjectState State = null!;
        public string LastTag = "";
        public bool Status;
        public bool KeyArrived;
        public int ScopeStart, ScopeCount;
        public List<int> Have = [];
        public List<int> FullSegments = [];
    }

    public async Task<GatewayResponse> ProcessAsync(GatewayRequest request, CancellationToken ct = default)
    {
        var messages = request.Messages ?? [];
        Interlocked.Increment(ref _batches);
        Interlocked.Add(ref _packets, messages.Length);

        var touched = new Dictionary<string, Touched>();
        var buffer = new byte[2048];

        foreach (var m in messages)
        {
            if (!TryDecode(m, buffer, out int length)) { engine.CountDropped(); continue; }
            if (!ClientPacket.TryParse(buffer.AsSpan(0, length), out var p)) { engine.CountDropped(); continue; }
            var state = engine.GetOrAdd(p, out bool keyArrived);
            if (state is null) { engine.CountDropped(); continue; }

            if (!touched.TryGetValue(state.Id, out var t)) touched[state.Id] = t = new Touched { State = state };
            t.LastTag = m.Tag;
            t.KeyArrived |= keyArrived;

            if (p.Type == PacketType.Status)
            {
                int seg = state.Layout.SegmentOf(Math.Min(p.Block, state.Layout.Blocks - 1));
                t.Status = true;
                t.ScopeStart = state.Layout.FirstBlockOf(seg);
                t.ScopeCount = state.Layout.BlocksIn(seg);
                continue;
            }

            var result = engine.Ingest(state, p.Block, p.Esi, p.Symbol, out bool segmentFull);
            switch (result)
            {
                case IngestResult.Dropped: engine.CountDropped(); break;
                case IngestResult.NewlyHave or IngestResult.AlreadyHave: t.Have.Add(p.Block); break;
            }
            if (segmentFull) t.FullSegments.Add(state.Layout.SegmentOf(p.Block));
        }

        // Single-part objects are committed before the reply, so the client learns of completion straight away.
        // Multi-part uploads run in the background and are reported by later STATUS polls.
        var inline = new List<Task>();
        foreach (var t in touched.Values)
        {
            foreach (var seg in t.FullSegments)
            {
                if (t.State.Layout.IsSinglePart) inline.Add(engine.CommitSegmentAsync(t.State, seg, ct));
                else _ = engine.CommitSegmentAsync(t.State, seg, CancellationToken.None);
            }
            if (t.Status) inline.Add(engine.RefreshAsync(t.State, ct));
            else if (t.KeyArrived)
            {
                // Segments that filled up before the key arrived are committed now.
                if (t.State.Layout.IsSinglePart) inline.Add(engine.CommitPendingAsync(t.State, ct));
                else _ = engine.CommitPendingAsync(t.State, CancellationToken.None);
            }
        }
        if (inline.Count > 0) await Task.WhenAll(inline);

        var replies = new List<ResponsePacket>(touched.Count);
        foreach (var t in touched.Values)
        {
            var runs = t.Status
                ? engine.HeldRuns(t.State, t.ScopeStart, t.ScopeCount)
                : t.State.Complete ? [(0, t.State.Layout.Blocks)] : HavePacket.ToRuns(t.Have.Order());
            if (runs.Count == 0 && !t.Status && !t.State.Complete) continue;
            var have = new HavePacket
            {
                TransferId = Convert.FromHexString(t.State.TransferHex),
                ObjectIdx = t.State.ObjectIdx,
                Complete = t.State.Complete,
                ScopeStart = t.Status ? t.ScopeStart : 0,
                ScopeCount = t.Status ? t.ScopeCount : 0,
                Runs = runs,
                Needs = t.Status ? engine.Needs(t.State, t.ScopeStart, t.ScopeCount) : [],
            };
            replies.Add(new(t.LastTag, Convert.ToBase64String(have.ToBytes())));
        }
        return new([.. replies]);
    }

    private static bool TryDecode(RequestPacket m, byte[] buffer, out int length)
    {
        length = 0;
        if (string.IsNullOrEmpty(m.Data) || string.IsNullOrEmpty(m.Tag)) return false;
        if (m.Formatter is not null && !string.Equals(m.Formatter, "base64", StringComparison.OrdinalIgnoreCase)) return false;
        return Convert.TryFromBase64Chars(m.Data, buffer, out length);
    }
}
