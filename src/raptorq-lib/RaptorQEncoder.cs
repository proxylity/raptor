namespace Proxylity.RaptorQ;

/// <summary>
/// RFC 6330 systematic RaptorQ encoder for a single source block.
///
/// Uses a pre-built <see cref="EncodingPlan"/> to compute the L intermediate symbols
/// by replaying a cached sequence of SIMD-accelerated row operations — avoiding the
/// cost of re-running the full solver for every source block with the same K.
/// </summary>
public class RaptorQEncoder
{
    private readonly RaptorQParameters _p;
    private readonly SymbolSlab _C;   // L intermediate symbols (post-solve, with reorder mapping)
    private readonly int _symbolSize;
    private readonly int _K;          // actual source symbol count

    /// <param name="sourceSymbols">
    ///   K source symbols, all of the same byte length.  May include zero-padded last symbol.
    /// </param>
    public RaptorQEncoder(ReadOnlyMemory<byte>[] sourceSymbols)
    {
        int K = sourceSymbols.Length;
        if (K == 0) throw new ArgumentException("At least one source symbol required.");
        _K = K;
        _symbolSize = sourceSymbols[0].Length;
        _p = RaptorQParameters.Compute(K);

        // D layout per RFC §5.4.2.1:
        //   rows 0..S-1          : zero vectors  (LDPC constraints)
        //   rows S..S+H-1        : zero vectors  (HDPC constraints)
        //   rows S+H..S+H+K-1   : source symbols
        //   rows S+H+K..S+H+K'-1: zero vectors  (padding for K' > K)
        var D = new SymbolSlab(_p.L, _symbolSize);
        for (int i = 0; i < K; i++)
            D.CopyFrom(_p.S + _p.H + i, sourceSymbols[i].Span);

        // Apply the cached encoding plan (replays the solver op sequence without
        // rebuilding the constraint matrix or re-running Gaussian elimination).
        EncodingPlan.GetCached(K).Apply(D);
        _C = D;
    }

    /// <summary>Number of source symbols K.</summary>
    public int K => _K;   // K' may differ; actual K is stored by caller

    /// <summary>
    /// Generates the encoded symbol for the given Encoding Symbol ID (ESI).
    /// ESI 0..K-1 are systematic (source symbol pass-through via XOR of C).
    /// ESI K.. are repair symbols.
    /// </summary>
    public Memory<byte> GenerateSymbol(int esi)
    {
        var result = new byte[_symbolSize];
        Span<int> indices = stackalloc int[64];
        // ISI = ESI for source symbols (ESI < K); ISI = ESI + (K'-K) for repair symbols.
        int isi = esi < _K ? esi : esi + (_p.K_prime - _K);
        RaptorQParameters.EncSymbolIndices(_p.K_prime, isi, _p, indices, out int count);

        for (int n = 0; n < count; n++)
        {
            int ci = indices[n];
            if (ci < _C.Count)
                RaptorQCodec.XorSymbols(result, _C.Get(ci));
        }
        return result;
    }
}