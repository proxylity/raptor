namespace Proxylity.RaptorQ;

/// <summary>
/// RFC 6330 §5.4.2 five-phase inactivation decoder / solver.
/// Solves A × C = D over GF(256) for the L×L sparse matrix A and L symbol vectors D,
/// returning the solved D slab (with a logical reorder mapping applied) or null
/// if A is singular.
///
/// All per-symbol operations (Scale, AddScaled, XorAssign) are dispatched through
/// <see cref="SymbolSlab"/> which uses SIMD-accelerated GF(256) arithmetic.
///
/// Used by both the encoder (to compute all L intermediate symbols from source+LDPC+HDPC)
/// and the decoder (to recover intermediate symbols from any sufficient received set).
///
/// Implementation notes:
///   - A is stored as a <see cref="SparseMatrix"/> (CSR-style).  The RaptorQ constraint
///     matrix is &lt;2% dense, so this reduces memory from O(L²) to O(L·d_avg) and
///     eliminates the O(L²) zero-scanning cost of the former dense representation.
///   - Phase 4 (BackSubstitute) only touches the greedy rows [0..i_val), not the
///     inactivated block [i_val..L).  The inactivated block was fully resolved during
///     Phase 3b (ForwardSubstituteInactivated) and touching it again is redundant.
/// </summary>
internal static class RaptorQSolver
{
    // -----------------------------------------------------------------------
    // Public entry point – dense-matrix overload kept for EncodingPlan recording
    // -----------------------------------------------------------------------

    /// <summary>
    /// Solves A × C = D for C using a dense byte[,] matrix (legacy path used by
    /// <see cref="EncodingPlan"/> recording, where the matrix is built once and
    /// the op sequence is replayed without re-solving).
    /// </summary>
    public static SymbolSlab? Solve(byte[,] A, SymbolSlab D, int L)
    {
        var sparse = DenseToSparse(A, L);
        return SolveSparse(sparse, D, L);
    }

    /// <summary>
    /// Solves A × C = D for C using a pre-built <see cref="SparseMatrix"/>.
    /// On success, returns the same <paramref name="D"/> slab with a logical reorder
    /// mapping applied so that <c>D.Get(j)</c> returns intermediate symbol j.
    /// Returns null if the system is rank-deficient.
    /// </summary>
    public static SymbolSlab? SolveSparse(SparseMatrix A, SymbolSlab D, int L)
    {
        var rowPerm = new int[L];
        var colPerm = new int[L];
        for (int i = 0; i < L; i++) { rowPerm[i] = i; colPerm[i] = i; }

        bool[] activeCol = new bool[L];
        for (int c = 0; c < L; c++) activeCol[c] = true;

        int i_val = 0;
        int u_val = 0;

        PeelDegreeOneRows(A, D, rowPerm, colPerm, activeCol, L, ref i_val);

        if (i_val < L && !ScheduleRows(A, D, rowPerm, colPerm, activeCol, L, ref i_val, ref u_val))
            return null;

        if (!GaussElimDense(A, D, rowPerm, colPerm, L, i_val))
            return null;

        ForwardSubstituteInactivated(A, D, rowPerm, colPerm, L, i_val);
        BackSubstitute(A, D, rowPerm, colPerm, L, i_val);
        BuildOutput(D, rowPerm, colPerm, L);
        return D;
    }

    // -----------------------------------------------------------------------
    // Row elimination helper — dispatches based on target × pivot row types.
    // Handles all four combinations of sparse/HDPC for target and pivot.
    // -----------------------------------------------------------------------
    private static void Eliminate(SparseMatrix A, int targetRow, int pivotRow, byte factor)
    {
        bool targetIsHdpc = targetRow >= A.HdpcStart && targetRow < A.HdpcEnd;
        bool pivotIsHdpc  = pivotRow  >= A.HdpcStart && pivotRow  < A.HdpcEnd;

        if (targetIsHdpc)
            A.EliminateIntoHdpc(targetRow, pivotRow, factor);       // any pivot → dense target
        else if (pivotIsHdpc)
            A.EliminateHdpcIntoSparse(targetRow, pivotRow, factor); // dense pivot → sparse target
        else
            A.EliminateSparseIntoSparse(targetRow, pivotRow, factor); // sparse pivot → sparse target
    }

    // -----------------------------------------------------------------------
    // Dense → Sparse conversion (used only by the EncodingPlan recording path)
    // -----------------------------------------------------------------------
    private static SparseMatrix DenseToSparse(byte[,] A, int L)
    {
        // No HDPC separation needed — EncodingPlan records the op sequence only.
        var S = new SparseMatrix(L, hdpcStart: L, hdpcEnd: L);
        for (int r = 0; r < L; r++)
            for (int c = 0; c < L; c++)
                if (A[r, c] != 0) S.Set(r, c, A[r, c]);
        return S;
    }

    // -----------------------------------------------------------------------
    // Fast pre-pass: repeatedly peel degree-1 rows before invoking the general
    // scheduler.  This handles BP-friendly cases without paying the full bucket
    // maintenance overhead of Phase 1.
    // -----------------------------------------------------------------------
    private static void PeelDegreeOneRows(SparseMatrix A, SymbolSlab D,
        int[] rowPerm, int[] colPerm, bool[] activeCol, int L, ref int i_val)
    {
        int[][] incidence = A.BuildSparseColumnIncidence();
        int[] rowDegree = new int[L];
        int[] rowPos = new int[L];
        int[] colPos = new int[L];
        bool[] queued = new bool[L];
        bool[] solvedRow = new bool[L];
        var queue = new Queue<int>();

        for (int slot = 0; slot < L; slot++)
        {
            rowPos[rowPerm[slot]] = slot;
            colPos[colPerm[slot]] = slot;
        }

        for (int row = 0; row < L; row++)
        {
            if (row >= A.HdpcStart && row < A.HdpcEnd)
                continue;

            rowDegree[row] = A.ActiveDegree(row, activeCol, out _);
            if (rowDegree[row] == 1)
            {
                queue.Enqueue(row);
                queued[row] = true;
            }
        }

        int i = i_val;

        while (queue.Count > 0 && i < L)
        {
            int pivRow = queue.Dequeue();
            queued[pivRow] = false;
            if (solvedRow[pivRow] || rowDegree[pivRow] != 1)
                continue;

            int pivRowSlot = rowPos[pivRow];
            if (pivRowSlot < i)
                continue;

            int deg = A.ActiveDegree(pivRow, activeCol, out int pivCol);
            if (deg != 1)
            {
                rowDegree[pivRow] = deg;
                if (deg == 1 && !queued[pivRow])
                {
                    queue.Enqueue(pivRow);
                    queued[pivRow] = true;
                }
                continue;
            }

            int pivColSlot = colPos[pivCol];
            if (pivColSlot < i || !activeCol[pivCol])
                continue;

            if (pivRowSlot != i)
            {
                int rowAtI = rowPerm[i];
                (rowPerm[pivRowSlot], rowPerm[i]) = (rowPerm[i], rowPerm[pivRowSlot]);
                rowPos[pivRow] = i;
                rowPos[rowAtI] = pivRowSlot;
            }

            if (pivColSlot != i)
            {
                int colAtI = colPerm[i];
                (colPerm[pivColSlot], colPerm[i]) = (colPerm[i], colPerm[pivColSlot]);
                colPos[pivCol] = i;
                colPos[colAtI] = pivColSlot;
            }

            byte pivVal = A.Get(pivRow, pivCol);
            if (pivVal == 0)
                break;

            activeCol[pivCol] = false;
            solvedRow[pivRow] = true;
            rowDegree[pivRow] = 0;

            if (pivVal != 1)
            {
                byte inv = GF256.Inverse(pivVal);
                A.ScaleRow(pivRow, inv);
                D.Scale(pivRow, inv);
            }

            foreach (int targetRow in incidence[pivCol])
            {
                if (targetRow == pivRow || solvedRow[targetRow])
                    continue;

                byte factor = A.Get(targetRow, pivCol);
                if (factor == 0) continue;

                D.AddScaled(targetRow, pivRow, factor);
                if (rowDegree[targetRow] > 0)
                {
                    rowDegree[targetRow]--;
                    if (rowDegree[targetRow] == 1 && !queued[targetRow])
                    {
                        queue.Enqueue(targetRow);
                        queued[targetRow] = true;
                    }
                }
            }

            i++;
        }

        i_val = i;
    }

    // -----------------------------------------------------------------------
    // Phase 1+2: Greedy row selection with inactivation.
    //
    // Uses a degree-bucket scheduler: rows are indexed by their active-column
    // degree so the minimum-degree row is found in O(1) amortised time.
    // Entries are lazily revalidated: when an entry is popped from the bucket
    // its real degree is recomputed; if it has changed it is re-inserted at the
    // correct level.
    //
    // "activeCol[c]" is true for both non-inactivated and non-pivoted columns.
    // ActiveDegree(row, activeCol) treats true as active.
    // -----------------------------------------------------------------------
    private static bool ScheduleRows(SparseMatrix A, SymbolSlab D,
        int[] rowPerm, int[] colPerm, bool[] activeCol,
        int L, ref int i_val, ref int u_val)
    {
        int i = i_val;
        int u = u_val;
        var colPos = new int[L];
        for (int slot = 0; slot < L; slot++)
            colPos[colPerm[slot]] = slot;

        // Degree-bucket structure: bucket[d] = list of row-perm slot indices ri
        // with current active degree d.
        const int MaxBucketDeg = 64;
        var buckets = new List<int>[MaxBucketDeg + 1];
        for (int d = 0; d <= MaxBucketDeg; d++) buckets[d] = new List<int>();

        for (int ri = i; ri < L; ri++)
        {
            int deg = A.ActiveDegree(rowPerm[ri], activeCol, out _);
            int clamped = Math.Min(deg, MaxBucketDeg);
            if (deg > 0) buckets[clamped].Add(ri);
        }

        while (i + u < L)
        {
            // Find minimum-degree active row in [i..L-u-1].
            int bestRi  = -1;
            int bestDeg = int.MaxValue;
            int bestFirstCol = -1; // absolute column index of first active non-zero in bestRow

            int d = 1;
            while (d <= MaxBucketDeg)
            {
                var bucket = buckets[d];
                bool reinserted = false;
                for (int bi = bucket.Count - 1; bi >= 0; bi--)
                {
                    int ri = bucket[bi];
                    // Evict entries that have moved outside the active window.
                    if (ri < i || ri >= L - u)
                    {
                        bucket.RemoveAt(bi);
                        continue;
                    }
                    // Lazily revalidate degree.
                    int realDeg = A.ActiveDegree(rowPerm[ri], activeCol, out int firstCol);
                    int clamped = Math.Min(realDeg, MaxBucketDeg);
                    if (clamped != d)
                    {
                        bucket.RemoveAt(bi);
                        if (realDeg > 0)
                        {
                            buckets[clamped].Add(ri);
                            // If entry moved to a lower bucket, restart scan.
                            if (clamped < d) { reinserted = true; }
                        }
                        continue;
                    }
                    if (realDeg < bestDeg)
                    {
                        bestDeg      = realDeg;
                        bestRi       = ri;
                        bestFirstCol = firstCol;
                    }
                }
                if (reinserted)
                {
                    // Re-inserted into a lower bucket; restart from d=1.
                    d = 1;
                    bestRi = -1;
                    bestDeg = int.MaxValue;
                    bestFirstCol = -1;
                    continue;
                }
                if (bestRi >= 0) break; // found at this level, no lower entries exist
                d++;
            }

            if (bestRi < 0)
            {
                // No row found — inactivate a column.
                int inactCol = -1;
                for (int ci = i; ci < L - u && inactCol < 0; ci++)
                {
                    int c = colPerm[ci];
                    if (!activeCol[c]) continue;
                    for (int ri = i; ri < L - u; ri++)
                    {
                        if (A.Get(rowPerm[ri], c) != 0) { inactCol = ci; break; }
                    }
                }
                if (inactCol < 0) return false;

                u++;
                int rightBound = L - u;
                int inactColIndex = colPerm[inactCol];
                int rightBoundCol = colPerm[rightBound];
                (colPerm[inactCol], colPerm[rightBound]) = (rightBoundCol, inactColIndex);
                colPos[inactColIndex] = rightBound;
                colPos[rightBoundCol] = inactCol;
                activeCol[colPerm[rightBound]] = false;
                continue;
            }

            // Bring the best row to slot i.
            (rowPerm[bestRi], rowPerm[i]) = (rowPerm[i], rowPerm[bestRi]);

            int bestColSlot = colPos[bestFirstCol];
            if (bestColSlot != i)
            {
                int colAtI = colPerm[i];
                (colPerm[bestColSlot], colPerm[i]) = (colPerm[i], colPerm[bestColSlot]);
                colPos[bestFirstCol] = i;
                colPos[colAtI] = bestColSlot;
            }

            int pivRow = rowPerm[i];
            int pivCol = colPerm[i];
            byte pivVal = A.Get(pivRow, pivCol);
            if (pivVal == 0) return false;

            // Mark pivot column inactive so ActiveDegree excludes it going forward.
            activeCol[pivCol] = false;

            if (pivVal != 1)
            {
                byte inv = GF256.Inverse(pivVal);
                A.ScaleRow(pivRow, inv);
                D.Scale(pivRow, inv);
            }

            for (int ri = i + 1; ri < L - u; ri++)
            {
                int r = rowPerm[ri];
                byte factor = A.Get(r, pivCol);
                if (factor == 0) continue;
                Eliminate(A, r, pivRow, factor);
                D.AddScaled(r, pivRow, factor);
            }

            i++;
        }

        i_val = i;
        u_val = u;
        return true;
    }

    // -----------------------------------------------------------------------
    // Phase 3: Dense Gaussian elimination on the inactivated sub-matrix
    // -----------------------------------------------------------------------
    private static bool GaussElimDense(SparseMatrix A, SymbolSlab D,
        int[] rowPerm, int[] colPerm, int L, int i_val)
    {
        int start = i_val;
        int end   = L;

        for (int step = 0; step < end - start; step++)
        {
            int diagIdx = start + step;
            int pivCol  = colPerm[diagIdx];

            int pivotRowIdx = -1;
            for (int ri = diagIdx; ri < end; ri++)
            {
                if (A.Get(rowPerm[ri], pivCol) != 0) { pivotRowIdx = ri; break; }
            }
            if (pivotRowIdx < 0) return false;

            (rowPerm[diagIdx], rowPerm[pivotRowIdx]) = (rowPerm[pivotRowIdx], rowPerm[diagIdx]);
            int pivRow = rowPerm[diagIdx];
            byte pivVal = A.Get(pivRow, pivCol);

            if (pivVal != 1)
            {
                byte inv = GF256.Inverse(pivVal);
                A.ScaleRow(pivRow, inv);
                D.Scale(pivRow, inv);
            }

            for (int ri = start; ri < end; ri++)
            {
                if (ri == diagIdx) continue;
                int r = rowPerm[ri];
                byte factor = A.Get(r, pivCol);
                if (factor == 0) continue;
                Eliminate(A, r, pivRow, factor);
                D.AddScaled(r, pivRow, factor);
            }
        }
        return true;
    }

    // -----------------------------------------------------------------------
    // Phase 3b: Forward-substitute inactivated solutions into greedy rows
    // -----------------------------------------------------------------------
    private static void ForwardSubstituteInactivated(SparseMatrix A, SymbolSlab D,
        int[] rowPerm, int[] colPerm, int L, int i_val)
    {
        for (int di = i_val; di < L; di++)
        {
            int pivRow = rowPerm[di];
            int pivCol = colPerm[di];

            for (int ri = 0; ri < i_val; ri++)
            {
                int r = rowPerm[ri];
                byte factor = A.Get(r, pivCol);
                if (factor == 0) continue;
                Eliminate(A, r, pivRow, factor);
                D.AddScaled(r, pivRow, factor);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Phase 4: Back-substitution through the greedy block only [0..i_val).
    //
    // The inactivated block [i_val..L) was fully resolved in Phase 3b.
    // Revisiting it here would be redundant work.
    // -----------------------------------------------------------------------
    private static void BackSubstitute(SparseMatrix A, SymbolSlab D,
        int[] rowPerm, int[] colPerm, int L, int i_val)
    {
        for (int step = i_val - 1; step >= 0; step--)
        {
            int pivRow = rowPerm[step];
            int pivCol = colPerm[step];

            for (int ri = 0; ri < step; ri++)
            {
                int r = rowPerm[ri];
                byte factor = A.Get(r, pivCol);
                if (factor == 0) continue;
                Eliminate(A, r, pivRow, factor);
                D.AddScaled(r, pivRow, factor);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Phase 5: Apply reorder mapping so D.Get(j) returns intermediate symbol j
    // -----------------------------------------------------------------------
    private static void BuildOutput(SymbolSlab D, int[] rowPerm, int[] colPerm, int L)
    {
        var mapping = new int[L];
        for (int i = 0; i < L; i++)
            mapping[colPerm[i]] = rowPerm[i];
        D.Reorder(mapping);
    }
}
