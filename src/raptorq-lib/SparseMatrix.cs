namespace Proxylity.RaptorQ;

/// <summary>
/// CSR-style sparse matrix over GF(256) for the L×L RaptorQ constraint matrix.
///
/// Each row stores its non-zero entries as parallel arrays of column indices and
/// values, kept in unsorted order for fast append during construction and fast
/// sequential scan during elimination.  The fill factor of the RaptorQ constraint
/// matrix is &lt;2%, so this typically uses ~40× less memory than a dense byte[L,L]
/// and eliminates the O(L²) zero-scanning cost in CollectNonzeros.
///
/// HDPC rows (rows <see cref="HdpcEnd"/>) are genuinely dense (every GF(256)
/// coefficient is potentially non-zero) and are kept in their own flat byte array
/// for vectorisable access; all other rows use the sparse representation.
/// </summary>
internal sealed class SparseMatrix
{
    // Per-row sparse storage (used for LDPC and LT rows)
    private readonly int[][] _cols;   // non-zero column indices per row
    private readonly byte[][] _vals;  // corresponding GF(256) values per row
    private readonly int[] _nnz;      // live non-zero count per row
    private readonly int[] _lookupStamp;
    private readonly int[] _lookupIndex;
    private int _lookupGeneration = 1;

    // Dense storage for HDPC rows [HdpcStart .. HdpcEnd)
    private readonly byte[,]? _dense;
    public readonly int HdpcStart;
    public readonly int HdpcEnd;
    public readonly int L;

    public SparseMatrix(int L, int hdpcStart, int hdpcEnd)
    {
        this.L = L;
        HdpcStart = hdpcStart;
        HdpcEnd   = hdpcEnd;

        _cols = new int[L][];
        _vals = new byte[L][];
        _nnz  = new int[L];
        _lookupStamp = new int[L];
        _lookupIndex = new int[L];

        // Allocate initial capacities.  LDPC rows have ~6 non-zeros each;
        // LT rows have up to ~40.  Over-allocate slightly to avoid resizing.
        for (int r = 0; r < L; r++)
        {
            if (r >= hdpcStart && r < hdpcEnd)
            {
                // HDPC: leave sparse arrays empty; dense block handles them.
                _cols[r] = Array.Empty<int>();
                _vals[r] = Array.Empty<byte>();
            }
            else if (r < hdpcStart)
            {
                // LDPC rows: ~6 entries
                _cols[r] = new int[8];
                _vals[r] = new byte[8];
            }
            else
            {
                // LT rows: up to 40 entries; capacity 48 to allow fill-in
                _cols[r] = new int[48];
                _vals[r] = new byte[48];
            }
        }

        if (hdpcEnd > hdpcStart)
            _dense = new byte[hdpcEnd - hdpcStart, L];
    }

    // -----------------------------------------------------------------------
    // Construction helpers (called during BuildConstraintMatrixSparse)
    // -----------------------------------------------------------------------

    /// <summary>XOR-adds <paramref name="val"/> at <paramref name="row"/>, <paramref name="col"/>.</summary>
    public void XorSet(int row, int col, byte val)
    {
        if (val == 0) return;

        if (row >= HdpcStart && row < HdpcEnd)
        {
            _dense![row - HdpcStart, col] ^= val;
            return;
        }

        // Search for existing entry
        int n = _nnz[row];
        int[] cols = _cols[row];
        for (int j = 0; j < n; j++)
        {
            if (cols[j] == col)
            {
                _vals[row][j] ^= val;
                if (_vals[row][j] == 0)
                {
                    // Remove by swapping with last
                    n--;
                    _nnz[row] = n;
                    _cols[row][j] = _cols[row][n];
                    _vals[row][j] = _vals[row][n];
                }
                return;
            }
        }
        // New entry
        if (n == cols.Length) Grow(row);
        _cols[row][n] = col;
        _vals[row][n] = val;
        _nnz[row] = n + 1;
    }

    /// <summary>Directly sets <paramref name="row"/>, <paramref name="col"/> = <paramref name="val"/>.
    /// Only call during construction when the entry is known to not exist yet.</summary>
    public void Set(int row, int col, byte val)
    {
        if (val == 0) return;

        if (row >= HdpcStart && row < HdpcEnd)
        {
            _dense![row - HdpcStart, col] = val;
            return;
        }

        int n = _nnz[row];
        if (n == _cols[row].Length) Grow(row);
        _cols[row][n] = col;
        _vals[row][n] = val;
        _nnz[row] = n + 1;
    }

    private void Grow(int row)
    {
        int newCap = Math.Max(8, _cols[row].Length * 2);
        Array.Resize(ref _cols[row], newCap);
        Array.Resize(ref _vals[row], newCap);
    }

    // -----------------------------------------------------------------------
    // Solver access
    // -----------------------------------------------------------------------

    /// <summary>Returns the GF(256) value at (row, col). O(nnz) for sparse rows, O(1) for HDPC.</summary>
    public byte Get(int row, int col)
    {
        if (row >= HdpcStart && row < HdpcEnd)
            return _dense![row - HdpcStart, col];

        int n = _nnz[row];
        int[] cols = _cols[row];
        for (int j = 0; j < n; j++)
            if (cols[j] == col) return _vals[row][j];
        return 0;
    }

    /// <summary>
    /// Copies the non-zero (col, val) pairs of <paramref name="row"/> into the caller's buffers.
    /// For HDPC rows, scans the dense row.
    /// Returns the count of non-zeros written.
    /// </summary>
    public int CollectNonzeros(int row, int[] colBuf, byte[] valBuf)
    {
        if (row >= HdpcStart && row < HdpcEnd)
        {
            int hdpcRow = row - HdpcStart;
            int n = 0;
            for (int c = 0; c < L; c++)
            {
                byte v = _dense![hdpcRow, c];
                if (v != 0) { colBuf[n] = c; valBuf[n] = v; n++; }
            }
            return n;
        }

        int nnz = _nnz[row];
        _cols[row].AsSpan(0, nnz).CopyTo(colBuf);
        _vals[row].AsSpan(0, nnz).CopyTo(valBuf);
        return nnz;
    }

    /// <summary>
    /// Returns the active-column degree of <paramref name="row"/>: the number of non-zero
    /// entries whose column index appears in <paramref name="activeColSet"/>.
    /// Also returns the index into <paramref name="activeColSet"/> of the first active non-zero
    /// via <paramref name="firstActiveColIdx"/> (-1 if degree is zero).
    /// </summary>
    public int ActiveDegree(int row, bool[] activeColSet, out int firstActiveColIdx)
    {
        firstActiveColIdx = -1;

        if (row >= HdpcStart && row < HdpcEnd)
        {
            int hdpcRow = row - HdpcStart;
            int deg = 0;
            for (int c = 0; c < L; c++)
            {
                if (activeColSet[c] && _dense![hdpcRow, c] != 0)
                {
                    if (firstActiveColIdx < 0) firstActiveColIdx = c;
                    deg++;
                }
            }
            return deg;
        }

        {
            int n = _nnz[row];
            int[] cols = _cols[row];
            int deg = 0;
            for (int j = 0; j < n; j++)
            {
                int c = cols[j];
                if (activeColSet[c])
                {
                    if (firstActiveColIdx < 0) firstActiveColIdx = c;
                    deg++;
                }
            }
            return deg;
        }
    }

    /// <summary>
    /// Eliminates the pivot row into the target row in GF(256):
    ///   target[c] ^= factor * pivot[c]  for all c in pivot's non-zeros.
    /// Both rows must be sparse (not HDPC).
    /// </summary>
    public void EliminateSparseIntoSparse(int targetRow, int pivotRow, byte factor)
    {
        int pn = _nnz[pivotRow];
        int[] pcols = _cols[pivotRow];
        byte[] pvals = _vals[pivotRow];
        int factorBase = factor * 256;
        int stamp = NextLookupGeneration();
        int tn = BuildSparseLookup(targetRow, stamp, out int[] tcols, out byte[] tvals);

        for (int j = 0; j < pn; j++)
        {
            int c = pcols[j];
            byte add = GF256.OctMul[factorBase + pvals[j]];
            if (add == 0) continue;

            if (_lookupStamp[c] == stamp)
            {
                int k = _lookupIndex[c];
                byte newVal = (byte)(tvals[k] ^ add);
                if (newVal == 0)
                {
                    int last = tn - 1;
                    int movedCol = tcols[last];
                    tcols[k] = movedCol;
                    tvals[k] = tvals[last];
                    tn = last;
                    _nnz[targetRow] = tn;
                    _lookupStamp[c] = 0;
                    if (k < tn)
                    {
                        _lookupIndex[movedCol] = k;
                    }
                }
                else
                {
                    tvals[k] = newVal;
                }
                continue;
            }

            if (tn == tcols.Length)
            {
                Grow(targetRow);
                tcols = _cols[targetRow];
                tvals = _vals[targetRow];
            }

            tcols[tn] = c;
            tvals[tn] = add;
            _lookupStamp[c] = stamp;
            _lookupIndex[c] = tn;
            tn++;
            _nnz[targetRow] = tn;
        }
    }

    /// <summary>
    /// Eliminates the pivot row (sparse or HDPC) into a dense HDPC target row:
    ///   target[c] ^= factor * pivot[c]  for all c in pivot's non-zeros.
    /// </summary>
    public void EliminateIntoHdpc(int targetRow, int pivotRow, byte factor)
    {
        int hdpcRow = targetRow - HdpcStart;
        int factorBase = factor * 256;

        if (pivotRow >= HdpcStart && pivotRow < HdpcEnd)
        {
            // Dense pivot into dense target
            int phdpc = pivotRow - HdpcStart;
            for (int c = 0; c < L; c++)
            {
                byte pv = _dense![phdpc, c];
                if (pv != 0)
                    _dense[hdpcRow, c] ^= GF256.OctMul[factorBase + pv];
            }
        }
        else
        {
            // Sparse pivot into dense target
            int pn = _nnz[pivotRow];
            int[] pcols = _cols[pivotRow];
            byte[] pvals = _vals[pivotRow];
            for (int j = 0; j < pn; j++)
            {
                byte add = GF256.OctMul[factorBase + pvals[j]];
                if (add != 0)
                    _dense![hdpcRow, pcols[j]] ^= add;
            }
        }
    }

    /// <summary>
    /// Eliminates an HDPC (dense) pivot row into a sparse target row:
    ///   target[c] ^= factor * pivot[c]  for all c.
    /// </summary>
    public void EliminateHdpcIntoSparse(int targetRow, int pivotRow, byte factor)
    {
        int phdpc = pivotRow - HdpcStart;
        int factorBase = factor * 256;
        int stamp = NextLookupGeneration();
        int tn = BuildSparseLookup(targetRow, stamp, out int[] tcols, out byte[] tvals);

        for (int c = 0; c < L; c++)
        {
            byte pv = _dense![phdpc, c];
            if (pv == 0) continue;
            byte add = GF256.OctMul[factorBase + pv];
            if (add == 0) continue;

            if (_lookupStamp[c] == stamp)
            {
                int k = _lookupIndex[c];
                byte newVal = (byte)(tvals[k] ^ add);
                if (newVal == 0)
                {
                    int last = tn - 1;
                    int movedCol = tcols[last];
                    tcols[k] = movedCol;
                    tvals[k] = tvals[last];
                    tn = last;
                    _nnz[targetRow] = tn;
                    _lookupStamp[c] = 0;
                    if (k < tn)
                    {
                        _lookupIndex[movedCol] = k;
                    }
                }
                else
                {
                    tvals[k] = newVal;
                }
                continue;
            }

            if (tn == tcols.Length)
            {
                Grow(targetRow);
                tcols = _cols[targetRow];
                tvals = _vals[targetRow];
            }

            tcols[tn] = c;
            tvals[tn] = add;
            _lookupStamp[c] = stamp;
            _lookupIndex[c] = tn;
            tn++;
            _nnz[targetRow] = tn;
        }
    }

    /// <summary>
    /// Scales row <paramref name="row"/> by <paramref name="scalar"/> in GF(256).
    /// </summary>
    public void ScaleRow(int row, byte scalar)
    {
        if (scalar == 1) return;

        if (row >= HdpcStart && row < HdpcEnd)
        {
            int hdpcRow = row - HdpcStart;
            int invBase = scalar * 256;
            for (int c = 0; c < L; c++)
            {
                byte v = _dense![hdpcRow, c];
                if (v != 0) _dense[hdpcRow, c] = GF256.OctMul[invBase + v];
            }
            return;
        }

        int n = _nnz[row];
        byte[] vals = _vals[row];
        int scalarBase = scalar * 256;
        for (int j = 0; j < n; j++)
            vals[j] = GF256.OctMul[scalarBase + vals[j]];
    }

    /// <summary>Clears the entire row (sets all entries to zero).</summary>
    public void ClearRow(int row)
    {
        if (row >= HdpcStart && row < HdpcEnd)
        {
            int hdpcRow = row - HdpcStart;
            for (int c = 0; c < L; c++) _dense![hdpcRow, c] = 0;
            return;
        }

        _nnz[row] = 0;
    }

    /// <summary>
    /// Replaces the non-zero structure of <paramref name="row"/> with the given sparse entries.
    /// Used during decode to overwrite an LT row's coefficients.
    /// </summary>
    public void SetSparseRow(int row, ReadOnlySpan<int> cols, int count)
    {
        _nnz[row] = 0;
        for (int n = 0; n < count; n++)
            XorSet(row, cols[n], 1);
    }

    /// <summary>
    /// Builds a sparse column-to-row incidence map for non-HDPC rows.
    /// Each entry lists the sparse rows that contain a non-zero in that column.
    /// </summary>
    public int[][] BuildSparseColumnIncidence()
    {
        var counts = new int[L];
        for (int row = 0; row < L; row++)
        {
            if (row >= HdpcStart && row < HdpcEnd)
                continue;

            int n = _nnz[row];
            int[] cols = _cols[row];
            for (int i = 0; i < n; i++)
                counts[cols[i]]++;
        }

        var incidence = new int[L][];
        for (int col = 0; col < L; col++)
            incidence[col] = new int[counts[col]];

        Array.Clear(counts);
        for (int row = 0; row < L; row++)
        {
            if (row >= HdpcStart && row < HdpcEnd)
                continue;

            int n = _nnz[row];
            int[] cols = _cols[row];
            for (int i = 0; i < n; i++)
            {
                int col = cols[i];
                incidence[col][counts[col]++] = row;
            }
        }

        return incidence;
    }

    private int NextLookupGeneration()
    {
        if (_lookupGeneration == int.MaxValue)
        {
            Array.Clear(_lookupStamp);
            _lookupGeneration = 1;
        }

        return _lookupGeneration++;
    }

    private int BuildSparseLookup(int targetRow, int stamp, out int[] tcols, out byte[] tvals)
    {
        int tn = _nnz[targetRow];
        tcols = _cols[targetRow];
        tvals = _vals[targetRow];
        for (int k = 0; k < tn; k++)
        {
            int c = tcols[k];
            _lookupStamp[c] = stamp;
            _lookupIndex[c] = k;
        }

        return tn;
    }
}
