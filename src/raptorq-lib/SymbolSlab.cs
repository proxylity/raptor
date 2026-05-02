namespace Proxylity.RaptorQ;

/// <summary>
/// Contiguous flat storage for <c>Count</c> symbol vectors each of <c>SymbolSize</c> bytes.
/// Symbol <c>i</c> occupies <c>_data[i*SymbolSize .. (i+1)*SymbolSize)</c>.
///
/// Compared to a jagged <c>byte[][]</c>, the single backing array gives much better
/// cache locality for the row operations (Scale, XorAssign, AddScaled) that dominate
/// the solver and encoder hot paths.
///
/// An optional logical-to-physical index mapping (set by <see cref="Reorder"/>) lets
/// the solver reorder the solution without copying any data.
///
/// When a <see cref="List{T}"/> recorder is attached via <see cref="StartRecording"/>,
/// every mutating operation appends the corresponding <see cref="SymbolOp"/> so that
/// the sequence can be replayed later by <see cref="EncodingPlan"/>.
/// </summary>
internal sealed class SymbolSlab
{
    private readonly byte[] _data;
    private int[]? _mapping;           // logical index → physical index
    private List<SymbolOp>? _recorder; // non-null only during plan generation

    public int Count { get; }
    public int SymbolSize { get; }

    public SymbolSlab(int count, int symbolSize)
    {
        Count = count;
        SymbolSize = symbolSize;
        _data = new byte[count * symbolSize];
    }

    // -----------------------------------------------------------------------
    // Index mapping
    // -----------------------------------------------------------------------

    private int Physical(int logical) =>
        _mapping != null ? _mapping[logical] : logical;

    /// <summary>
    /// Establishes a logical-to-physical index mapping (zero-copy).
    /// After calling this, <c>Get(logical)</c> returns the symbol at physical index
    /// <c>mapping[logical]</c>.  <see cref="Scale"/>, <see cref="XorAssign"/>, and
    /// <see cref="AddScaled"/> continue to use the physical row indices directly (as
    /// the solver always operates on physical rows), so mapping does not affect them.
    /// </summary>
    public void Reorder(int[] mapping)
    {
        _mapping = mapping;
        _recorder?.Add(SymbolOp.Reorder(mapping));
    }

    // -----------------------------------------------------------------------
    // Read / write access
    // -----------------------------------------------------------------------

    /// <summary>Read-only view of symbol at logical index <paramref name="i"/>.</summary>
    public ReadOnlySpan<byte> Get(int i)
    {
        int phys = Physical(i);
        return _data.AsSpan(phys * SymbolSize, SymbolSize);
    }

    /// <summary>Copies <paramref name="src"/> into the slot at physical index <paramref name="i"/>.</summary>
    public void CopyFrom(int i, ReadOnlySpan<byte> src) =>
        src.CopyTo(_data.AsSpan(i * SymbolSize, SymbolSize));

    // -----------------------------------------------------------------------
    // Row operations (operate on physical indices; SIMD-accelerated)
    // -----------------------------------------------------------------------

    /// <summary><c>D[dest] *= scalar</c> in GF(256).</summary>
    public void Scale(int dest, byte scalar)
    {
        if (scalar == 1) return;
        RaptorQCodec.ScaleSymbolSimd(_data.AsSpan(dest * SymbolSize, SymbolSize), scalar);
        _recorder?.Add(SymbolOp.MulAssign(dest, scalar));
    }

    /// <summary><c>D[dest] ^= D[src]</c> (GF(2) XOR).</summary>
    public void XorAssign(int dest, int src)
    {
        // Both slices come from the same backing array but cannot overlap when dest != src.
        RaptorQCodec.XorSymbols(
            _data.AsSpan(dest * SymbolSize, SymbolSize),
            _data.AsSpan(src  * SymbolSize, SymbolSize));
        _recorder?.Add(SymbolOp.AddAssign(dest, src));
    }

    /// <summary><c>D[dest] ^= scalar * D[src]</c> in GF(256).</summary>
    public void AddScaled(int dest, int src, byte scalar)
    {
        if (scalar == 1)
        {
            XorAssign(dest, src);
            return;
        }
        RaptorQCodec.XorScaleSymbolSimd(
            _data.AsSpan(dest * SymbolSize, SymbolSize),
            _data.AsSpan(src  * SymbolSize, SymbolSize),
            scalar);
        _recorder?.Add(SymbolOp.FMA(dest, src, scalar));
    }

    // -----------------------------------------------------------------------
    // Recording support (for EncodingPlan generation)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Attaches a recorder.  Every subsequent mutating call also appends the
    /// corresponding <see cref="SymbolOp"/> to <paramref name="ops"/>.
    /// </summary>
    public void StartRecording(List<SymbolOp> ops) => _recorder = ops;
}
