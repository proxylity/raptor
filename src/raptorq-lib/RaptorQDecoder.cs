namespace Proxylity.RaptorQ;

/// <summary>
/// RFC 6330 systematic RaptorQ decoder for a single source block.
///
/// Recovers K source symbols from any K or more linearly-independent received
/// encoded symbols using the inactivation decoder (§5.4.2).
/// </summary>
public class RaptorQDecoder
{
    private const int SmallErasureThreshold = 32;

    private readonly int _K;
    private readonly int _symbolSize;
    private byte[][]? _sourceSymbols;

    /// <param name="K">Number of source symbols.</param>
    /// <param name="symbolSize">Bytes per symbol.</param>
    public RaptorQDecoder(int K, int symbolSize)
    {
        _K          = K;
        _symbolSize = symbolSize;
    }

    /// <summary>
    /// Attempts to recover all K source symbols from the received encoded symbols.
    /// </summary>
    /// <param name="receivedSymbols">Raw symbol data per received packet.</param>
    /// <param name="receivedEsi">ESI (packet_index field) matching each symbol.</param>
    /// <returns>true on success; false if the system is rank-deficient.</returns>
    public bool Decode(byte[][] receivedSymbols, int[] receivedEsi)
    {
        if (receivedSymbols.Length != receivedEsi.Length)
            throw new ArgumentException("Symbol and ESI arrays must be the same length.");

        int rxCount = receivedSymbols.Length;
        if (rxCount < _K) return false;

        if (TryDecodeSystematic(receivedSymbols, receivedEsi))
            return true;

        if (TryDecodeSmallSystematicErasure(receivedSymbols, receivedEsi))
            return true;

        var p = RaptorQParameters.Compute(_K);
        int L = p.L;

        // Build the sparse L×L constraint matrix (LDPC rows 0..S-1, HDPC rows S..S+H-1,
        // LT rows S+H..L-1 for ISI 0..K'-1) exactly as the encoder does.
        // This provides the S+H pre-coding constraint equations plus K' LT equations.
        var A = RaptorQCodec.BuildConstraintMatrixSparse(p);

        // D is L symbol vectors, initially all zeros (correct for LDPC/HDPC rows and
        // for padding symbols ISI K..K'-1 whose value is defined to be zero).
        var D = new SymbolSlab(L, _symbolSize);

        // Track which LT-row slots (ISI 0..K'-1) have been filled by received symbols.
        var rowUsed = new bool[p.K_prime];

        // Pre-mark padding ISI slots (K..K'-1) as used: their D=0 is already correct
        // and they must not be consumed by repair symbols.
        for (int isi = _K; isi < p.K_prime; isi++)
            rowUsed[isi] = true;

        // Deferred list for repair symbols with ESI >= K (no natural source slot).
        var deferred = new System.Collections.Generic.List<(int esi, byte[] sym)>();

        Span<int> indices = stackalloc int[64];

        // First pass: assign received symbols with ESI < K to their natural source rows.
        // ESI K..K'-1 are repair symbols (padding ISIs are already handled as D=0).
        for (int i = 0; i < rxCount; i++)
        {
            int esi = receivedEsi[i];
            if (esi < _K)
            {
                D.CopyFrom(p.S + p.H + esi, receivedSymbols[i]);
                rowUsed[esi] = true;
            }
            else
            {
                deferred.Add((esi, receivedSymbols[i]));
            }
        }

        // Second pass: place repair symbols (ESI >= K) into unused source LT-row slots.
        // Free slots are source ISI values (0..K-1) whose row hasn't been filled.
        int freeSlot = 0;
        foreach (var (esi, sym) in deferred)
        {
            while (freeSlot < p.K_prime && rowUsed[freeSlot]) freeSlot++;
            if (freeSlot >= p.K_prime) break;   // More repair than free slots; extra ignored.

            int destRow = p.S + p.H + freeSlot;
            rowUsed[freeSlot] = true;
            freeSlot++;

            // Overwrite the LT coefficients for the original ISI with those of this repair symbol.
            // ISI = ESI + (K'-K) for repair symbols (RFC 6330 §5.3.3.2).
            int isi = esi + (p.K_prime - _K);
            RaptorQParameters.EncSymbolIndices(p.K_prime, isi, p, indices, out int cnt);
            A.SetSparseRow(destRow, indices, cnt);

            D.CopyFrom(destRow, sym);
        }

        var C = RaptorQSolver.SolveSparse(A, D, L);
        if (C == null) return false;

        // Recover source symbols by encoding from intermediate symbols (same as encoder GenerateSymbol).
        // For a systematic code, Enc(ISI=k, C) = source_k for k = 0..K-1.
        _sourceSymbols = new byte[_K][];
        Span<int> srcIdx = stackalloc int[64];
        for (int k = 0; k < _K; k++)
        {
            var sym = new byte[_symbolSize];
            RaptorQParameters.EncSymbolIndices(p.K_prime, k, p, srcIdx, out int cnt);
            for (int n = 0; n < cnt; n++)
                RaptorQCodec.XorSymbols(sym, C.Get(srcIdx[n]));
            _sourceSymbols[k] = sym;
        }

        return true;
    }

    private bool TryDecodeSystematic(byte[][] receivedSymbols, int[] receivedEsi)
    {
        var direct = new byte[_K][];
        int found = 0;

        for (int i = 0; i < receivedEsi.Length; i++)
        {
            int esi = receivedEsi[i];
            if ((uint)esi >= (uint)_K || direct[esi] != null)
                continue;

            byte[] symbol = receivedSymbols[i];
            if (symbol.Length != _symbolSize)
                throw new ArgumentException($"Received symbol {i} has size {symbol.Length}, expected {_symbolSize}.");

            direct[esi] = symbol;
            found++;
        }

        if (found != _K)
            return false;

        _sourceSymbols = new byte[_K][];
        for (int i = 0; i < _K; i++)
            _sourceSymbols[i] = direct[i]!.ToArray();

        return true;
    }

    private bool TryDecodeSmallSystematicErasure(byte[][] receivedSymbols, int[] receivedEsi)
    {
        var direct = new byte[_K][];
        var repairSymbols = new List<(int esi, byte[] sym)>();
        var seenRepair = new HashSet<int>();
        int found = 0;

        for (int i = 0; i < receivedEsi.Length; i++)
        {
            int esi = receivedEsi[i];
            byte[] symbol = receivedSymbols[i];
            if (symbol.Length != _symbolSize)
                throw new ArgumentException($"Received symbol {i} has size {symbol.Length}, expected {_symbolSize}.");

            if ((uint)esi < (uint)_K)
            {
                if (direct[esi] == null)
                {
                    direct[esi] = symbol;
                    found++;
                }
            }
            else if (seenRepair.Add(esi))
            {
                repairSymbols.Add((esi, symbol));
            }
        }

        int missingCount = _K - found;
        if (missingCount <= 0 || missingCount > SmallErasureThreshold || repairSymbols.Count < missingCount)
            return false;

        int[] missingIndices = new int[missingCount];
        for (int i = 0, n = 0; i < _K; i++)
        {
            if (direct[i] == null)
                missingIndices[n++] = i;
        }

        byte[,] candidateCoefficients = BuildSmallErasureCoefficientMatrix(missingIndices, repairSymbols);
        int[] selectedRows = SelectIndependentRows(candidateCoefficients, repairSymbols.Count, missingCount);
        if (selectedRows.Length < missingCount)
            return false;

        byte[,] smallA = new byte[missingCount, missingCount];
        var smallD = new SymbolSlab(missingCount, _symbolSize);

        var knownOnly = new ReadOnlyMemory<byte>[_K];
        byte[] zeroSymbol = new byte[_symbolSize];
        for (int i = 0; i < _K; i++)
            knownOnly[i] = direct[i] != null ? direct[i] : zeroSymbol;
        var knownEncoder = new RaptorQEncoder(knownOnly);

        for (int row = 0; row < missingCount; row++)
        {
            int selected = selectedRows[row];
            for (int col = 0; col < missingCount; col++)
                smallA[row, col] = candidateCoefficients[selected, col];

            var residual = repairSymbols[selected].sym.ToArray();
            RaptorQCodec.XorSymbols(residual, knownEncoder.GenerateSymbol(repairSymbols[selected].esi).Span);
            smallD.CopyFrom(row, residual);
        }

        SymbolSlab? solved = RaptorQSolver.Solve(smallA, smallD, missingCount);
        if (solved == null)
            return false;

        _sourceSymbols = new byte[_K][];
        for (int i = 0; i < _K; i++)
            _sourceSymbols[i] = direct[i] != null ? direct[i]!.ToArray() : Array.Empty<byte>();
        for (int i = 0; i < missingCount; i++)
            _sourceSymbols[missingIndices[i]] = solved.Get(i).ToArray();

        return true;
    }

    private byte[,] BuildSmallErasureCoefficientMatrix(int[] missingIndices, List<(int esi, byte[] sym)> repairSymbols)
    {
        int missingCount = missingIndices.Length;
        int repairCount = repairSymbols.Count;
        var coefficients = new byte[repairCount, missingCount];

        // Build all coefficient columns simultaneously with a single EncodingPlan replay.
        // The slab is missingCount bytes wide: byte [col] in each row tracks the GF(256)
        // coefficient of source symbol missingIndices[col] in that intermediate symbol.
        var p = RaptorQParameters.Compute(_K);
        var D = new SymbolSlab(p.L, missingCount);

        Span<byte> oneHot = stackalloc byte[missingCount]; // all zeros
        for (int col = 0; col < missingCount; col++)
        {
            oneHot[col] = 1;
            D.CopyFrom(p.S + p.H + missingIndices[col], oneHot);
            oneHot[col] = 0;
        }

        EncodingPlan.GetCached(_K).Apply(D);

        Span<int> indices = stackalloc int[64];
        Span<byte> result = stackalloc byte[missingCount];
        for (int row = 0; row < repairCount; row++)
        {
            int esi = repairSymbols[row].esi;
            int isi = esi < _K ? esi : esi + (p.K_prime - _K);
            RaptorQParameters.EncSymbolIndices(p.K_prime, isi, p, indices, out int count);

            result.Clear();
            for (int n = 0; n < count; n++)
            {
                int ci = indices[n];
                if (ci < p.L)
                    RaptorQCodec.XorSymbols(result, D.Get(ci));
            }
            for (int col = 0; col < missingCount; col++)
                coefficients[row, col] = result[col];
        }

        return coefficients;
    }

    private static int[] SelectIndependentRows(byte[,] coefficients, int rowCount, int columnCount)
    {
        var selected = new List<int>(columnCount);
        int currentRank = 0;

        for (int row = 0; row < rowCount && selected.Count < columnCount; row++)
        {
            int nextRank = ComputeRankWithCandidate(coefficients, selected, row, columnCount);
            if (nextRank > currentRank)
            {
                selected.Add(row);
                currentRank = nextRank;
            }
        }

        return selected.ToArray();
    }

    private static int ComputeRankWithCandidate(byte[,] coefficients, List<int> selectedRows, int candidateRow, int columnCount)
    {
        int rowCount = selectedRows.Count + 1;
        var work = new byte[rowCount, columnCount];

        for (int r = 0; r < selectedRows.Count; r++)
        {
            int sourceRow = selectedRows[r];
            for (int c = 0; c < columnCount; c++)
                work[r, c] = coefficients[sourceRow, c];
        }

        for (int c = 0; c < columnCount; c++)
            work[selectedRows.Count, c] = coefficients[candidateRow, c];

        int rank = 0;
        for (int col = 0; col < columnCount && rank < rowCount; col++)
        {
            int pivot = rank;
            while (pivot < rowCount && work[pivot, col] == 0) pivot++;
            if (pivot == rowCount) continue;

            if (pivot != rank)
            {
                for (int c = col; c < columnCount; c++)
                    (work[rank, c], work[pivot, c]) = (work[pivot, c], work[rank, c]);
            }

            byte inv = GF256.Inverse(work[rank, col]);
            for (int c = col; c < columnCount; c++)
                work[rank, c] = GF256.Multiply(work[rank, c], inv);

            for (int r = 0; r < rowCount; r++)
            {
                if (r == rank) continue;
                byte factor = work[r, col];
                if (factor == 0) continue;
                for (int c = col; c < columnCount; c++)
                    work[r, c] ^= GF256.Multiply(factor, work[rank, c]);
            }

            rank++;
        }

        return rank;
    }

    /// <summary>
    /// Concatenates all recovered source symbols, optionally trimming zero-padding.
    /// Call only after a successful Decode().
    /// </summary>
    /// <param name="originalSize">
    ///   Original object size in bytes.  When &gt;= 0 the result is sliced to this length.
    ///   Pass -1 to return the full K * symbolSize bytes.
    /// </param>
    public byte[] ReconstructOriginalObject(long originalSize = -1)
    {
        if (_sourceSymbols == null)
            throw new InvalidOperationException("Decode() must succeed before calling ReconstructOriginalObject().");

        var full = new byte[(long)_K * _symbolSize];
        for (int i = 0; i < _K; i++)
            _sourceSymbols[i].CopyTo(full, (long)i * _symbolSize);

        return originalSize >= 0 ? full[..(int)originalSize] : full;
    }
}