using System.Collections.Concurrent;

namespace Proxylity.RaptorQ;

/// <summary>
/// Pre-computed sequence of symbol operations that solve A × C = D for a fixed K.
/// <para>
/// The row-operation sequence is fully determined by the constraint matrix A, which
/// depends only on K (source symbol count).  By recording the sequence once with
/// 1-byte dummy symbols and replaying it on every source block with the same K, the
/// expensive <c>BuildConstraintMatrix + Solve</c> work is replaced by a direct pass
/// over the recorded operation list — giving a 2–3× encoding throughput improvement
/// when the same K is used repeatedly.
/// </para>
/// </summary>
public sealed class EncodingPlan
{
    private static readonly ConcurrentDictionary<int, EncodingPlan> _cache = new();

    private readonly SymbolOp[] _ops;

    internal int K { get; }

    private EncodingPlan(int k, SymbolOp[] ops) { K = k; _ops = ops; }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns a cached plan for source symbol count <paramref name="K"/>,
    /// generating it on first use.  Thread-safe.
    /// </summary>
    public static EncodingPlan GetCached(int K) =>
        _cache.GetOrAdd(K, k => Generate(k));

    /// <summary>Generates a fresh plan for source symbol count <paramref name="K"/>.</summary>
    public static EncodingPlan Generate(int K)
    {
        var p = RaptorQParameters.Compute(K);
        var A = RaptorQCodec.BuildConstraintMatrix(p);

        // Use 1-byte dummy symbols — the matrix structure is independent of symbol size/content.
        var D = new SymbolSlab(p.L, 1);
        var ops = new List<SymbolOp>();
        D.StartRecording(ops);

        // Solve records every D operation into ops; A and permutations are discarded.
        RaptorQSolver.Solve(A, D, p.L);

        return new EncodingPlan(K, ops.ToArray());
    }

    // -----------------------------------------------------------------------
    // Internal: replay
    // -----------------------------------------------------------------------

    /// <summary>
    /// Applies the recorded operation sequence to <paramref name="D"/>.
    /// <paramref name="D"/> must be pre-initialised with source symbols in the same
    /// layout as the encoder (rows S+H .. S+H+K-1 contain source symbol data;
    /// all other rows are zero).
    /// </summary>
    internal void Apply(SymbolSlab D)
    {
        foreach (ref readonly var op in _ops.AsSpan())
        {
            switch (op.Kind)
            {
                case SymbolOpKind.AddAssign:
                    D.XorAssign(op.Dest, op.Src);
                    break;
                case SymbolOpKind.MulAssign:
                    D.Scale(op.Dest, op.Scalar);
                    break;
                case SymbolOpKind.FMA:
                    D.AddScaled(op.Dest, op.Src, op.Scalar);
                    break;
                case SymbolOpKind.Reorder:
                    D.Reorder(op.Mapping!);
                    break;
            }
        }
    }
}
