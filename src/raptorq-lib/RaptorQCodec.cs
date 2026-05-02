using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Proxylity.RaptorQ;

/// <summary>
/// Shared encode/decode utilities: RFC 6330 constraint matrix construction and SIMD XOR.
/// </summary>
public static class RaptorQCodec
{
    // -----------------------------------------------------------------------
    // RFC §5.3.3.3 – Build the L×L constraint matrix A over GF(256).
    //
    // Row layout (top to bottom):
    //   Rows  0 .. S-1          : LDPC  (§5.3.3.3)
    //   Rows  S .. S+H-1        : HDPC  (§5.3.3.4)
    //   Rows  S+H .. S+H+K'-1  : LT encoded symbols
    //
    // Column layout:
    //   Cols  0 .. W-1          : LT intermediate symbols
    //   Cols  W .. W+P-1        : PI intermediate symbols
    // -----------------------------------------------------------------------
    internal static byte[,] BuildConstraintMatrix(in RaptorQParameters p)
    {
        var A = new byte[p.L, p.L];
        BuildLdpcRows(A, p);
        BuildHdpcRows(A, p);
        BuildLtRows(A, p);
        return A;
    }

    /// <summary>
    /// Builds the L×L constraint matrix as a <see cref="SparseMatrix"/>.
    /// LDPC and LT rows (~1–2% fill) are stored sparsely; HDPC rows are kept dense.
    /// </summary>
    internal static SparseMatrix BuildConstraintMatrixSparse(in RaptorQParameters p)
    {
        var A = new SparseMatrix(p.L, hdpcStart: p.S, hdpcEnd: p.S + p.H);
        BuildLdpcRowsSparse(A, p);
        BuildHdpcRowsSparse(A, p);
        BuildLtRowsSparse(A, p);
        return A;
    }

    // RFC §5.3.3.3 – LDPC rows
    private static void BuildLdpcRows(byte[,] A, in RaptorQParameters p)
    {
        // G_LDPC,1: 3 entries per source column i in [0,B), a = 1 + i/S
        for (int i = 0; i < p.B; i++)
        {
            int a = 1 + i / p.S;
            int b = i % p.S;
            A[b, i] ^= 1;
            b = (b + a) % p.S; A[b, i] ^= 1;
            b = (b + a) % p.S; A[b, i] ^= 1;
        }
        // I_S: identity for LDPC symbol columns B..W-1
        for (int j = 0; j < p.S; j++)
            A[j, p.B + j] ^= 1;
        // G_LDPC,2: 2 entries per LDPC row in PI columns
        for (int i = 0; i < p.S; i++)
        {
            A[i, p.W + (i % p.P)] ^= 1;
            A[i, p.W + ((i + 1) % p.P)] ^= 1;
        }
    }

    // RFC §5.3.3.4 – HDPC rows: G_HDPC = MT × GAMMA, followed by I_H
    private static void BuildHdpcRows(byte[,] A, in RaptorQParameters p)
    {
        int KS = p.K_prime + p.S;
        // Build MT × GAMMA using the recursive right-to-left formulation.
        var result = new byte[p.H, KS];
        // Seed last column: result[i, KS-1] = alpha^i
        for (int i = 0; i < p.H; i++)
            result[i, KS - 1] = GF256.AlphaPow(i);
        // Fill right-to-left: result[i,j] = alpha * result[i,j+1], then XOR MT bits
        for (int j = KS - 2; j >= 0; j--)
        {
            for (int i = 0; i < p.H; i++)
                result[i, j] = GF256.Multiply(2, result[i, j + 1]);  // alpha = 2 in GF(256)
            int r6 = (int)RaptorQParameters.Rand((uint)(j + 1), 6, (uint)p.H);
            int r7 = (int)RaptorQParameters.Rand((uint)(j + 1), 7, (uint)(p.H - 1));
            result[r6, j] ^= 1;
            result[(r6 + r7 + 1) % p.H, j] ^= 1;
        }
        // Copy G_HDPC into A, rows p.S..p.S+p.H-1, cols 0..KS-1
        for (int i = 0; i < p.H; i++)
            for (int j = 0; j < KS; j++)
                A[p.S + i, j] = result[i, j];
        // I_H: identity block in cols KS..KS+H-1
        for (int i = 0; i < p.H; i++)
            A[p.S + i, KS + i] = 1;
    }

    // RFC §5.3.3.3 – LT rows via Enc[]/Tuple for ISI 0..K'-1
    private static void BuildLtRows(byte[,] A, in RaptorQParameters p)
    {
        Span<int> indices = stackalloc int[64];
        for (int isi = 0; isi < p.K_prime; isi++)
        {
            int destRow = p.S + p.H + isi;
            RaptorQParameters.EncSymbolIndices(p.K_prime, isi, p, indices, out int count);
            for (int n = 0; n < count; n++)
                A[destRow, indices[n]] ^= 1;
        }
    }

    // -----------------------------------------------------------------------
    // Sparse constraint-matrix builders (mirror of the dense builders above)
    // -----------------------------------------------------------------------

    private static void BuildLdpcRowsSparse(SparseMatrix A, in RaptorQParameters p)
    {
        for (int i = 0; i < p.B; i++)
        {
            int a = 1 + i / p.S;
            int b = i % p.S;
            A.XorSet(b, i, 1);
            b = (b + a) % p.S; A.XorSet(b, i, 1);
            b = (b + a) % p.S; A.XorSet(b, i, 1);
        }
        for (int j = 0; j < p.S; j++)
            A.XorSet(j, p.B + j, 1);
        for (int i = 0; i < p.S; i++)
        {
            A.XorSet(i, p.W + (i % p.P), 1);
            A.XorSet(i, p.W + ((i + 1) % p.P), 1);
        }
    }

    private static void BuildHdpcRowsSparse(SparseMatrix A, in RaptorQParameters p)
    {
        // HDPC rows are always dense — build into a temp dense block then copy.
        int KS = p.K_prime + p.S;
        var result = new byte[p.H, KS];
        for (int i = 0; i < p.H; i++)
            result[i, KS - 1] = GF256.AlphaPow(i);
        for (int j = KS - 2; j >= 0; j--)
        {
            for (int i = 0; i < p.H; i++)
                result[i, j] = GF256.Multiply(2, result[i, j + 1]);
            int r6 = (int)RaptorQParameters.Rand((uint)(j + 1), 6, (uint)p.H);
            int r7 = (int)RaptorQParameters.Rand((uint)(j + 1), 7, (uint)(p.H - 1));
            result[r6, j] ^= 1;
            result[(r6 + r7 + 1) % p.H, j] ^= 1;
        }
        for (int i = 0; i < p.H; i++)
        {
            for (int j = 0; j < KS; j++)
                A.Set(p.S + i, j, result[i, j]);
            A.Set(p.S + i, KS + i, 1);
        }
    }

    private static void BuildLtRowsSparse(SparseMatrix A, in RaptorQParameters p)
    {
        Span<int> indices = stackalloc int[64];
        for (int isi = 0; isi < p.K_prime; isi++)
        {
            int destRow = p.S + p.H + isi;
            RaptorQParameters.EncSymbolIndices(p.K_prime, isi, p, indices, out int count);
            for (int n = 0; n < count; n++)
                A.XorSet(destRow, indices[n], 1);
        }
    }

    // -----------------------------------------------------------------------
    // SIMD XOR: XORs source into target in-place.
    // -----------------------------------------------------------------------
    public static void XorSymbols(Span<byte> target, ReadOnlySpan<byte> source)
    {
        ref byte tRef = ref MemoryMarshal.GetReference(target);
        ref byte sRef = ref MemoryMarshal.GetReference(source);
        int length = target.Length;
        int i = 0;

        if (Vector512.IsHardwareAccelerated)
        {
            int end = length - Vector512<byte>.Count;
            for (; i <= end; i += Vector512<byte>.Count)
            {
                var vt = Vector512.LoadUnsafe(ref tRef, (nuint)i);
                var vs = Vector512.LoadUnsafe(ref sRef, (nuint)i);
                Vector512.StoreUnsafe(vt ^ vs, ref tRef, (nuint)i);
            }
        }

        if (Vector256.IsHardwareAccelerated)
        {
            int end = length - Vector256<byte>.Count;
            for (; i <= end; i += Vector256<byte>.Count)
            {
                var vt = Vector256.LoadUnsafe(ref tRef, (nuint)i);
                var vs = Vector256.LoadUnsafe(ref sRef, (nuint)i);
                Vector256.StoreUnsafe(vt ^ vs, ref tRef, (nuint)i);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            int end = length - Vector128<byte>.Count;
            for (; i <= end; i += Vector128<byte>.Count)
            {
                var vt = Vector128.LoadUnsafe(ref tRef, (nuint)i);
                var vs = Vector128.LoadUnsafe(ref sRef, (nuint)i);
                Vector128.StoreUnsafe(vt ^ vs, ref tRef, (nuint)i);
            }
        }

        for (; i < length; i++)
            Unsafe.Add(ref tRef, i) ^= Unsafe.Add(ref sRef, i);
    }

    // -----------------------------------------------------------------------
    // SIMD GF(256) scalar multiply using the PSHUFB nibble-table technique.
    // See "Screaming Fast Galois Field Arithmetic Using Intel SIMD Instructions"
    // by Plank et al.  Dispatch order: AVX2 (32B) → SSSE3 (16B) → scalar.
    // -----------------------------------------------------------------------

    /// <summary>sym[i] = GF256.Multiply(scalar, sym[i]) for all i, in-place.</summary>
    internal static void ScaleSymbolSimd(Span<byte> sym, byte scalar)
    {
        if (scalar == 1) return;
        if (scalar == 0) { sym.Clear(); return; }

        ref byte symRef = ref MemoryMarshal.GetReference(sym);
        ref byte loBase = ref MemoryMarshal.GetArrayDataReference(GF256.MulLow);
        ref byte hiBase = ref MemoryMarshal.GetArrayDataReference(GF256.MulHi);
        int length = sym.Length;
        int i = 0;

        if (Avx2.IsSupported)
        {
            var lo128 = Vector128.LoadUnsafe(ref loBase, (nuint)(scalar * 16));
            var hi128 = Vector128.LoadUnsafe(ref hiBase, (nuint)(scalar * 16));
            var loTable = Vector256.Create(lo128, lo128);
            var hiTable = Vector256.Create(hi128, hi128);
            var loMask = Vector256.Create((byte)0x0F);
            var hiMask = Vector256.Create((byte)0xF0);
            int end = length - 31;
            for (; i <= end; i += 32)
            {
                var v = Vector256.LoadUnsafe(ref symRef, (nuint)i);
                var lo = Avx2.Shuffle(loTable, v & loMask);
                var hiNibbles = Avx2.ShiftRightLogical((v & hiMask).AsInt64(), 4).AsByte();
                var hi = Avx2.Shuffle(hiTable, hiNibbles);
                Vector256.StoreUnsafe(lo ^ hi, ref symRef, (nuint)i);
            }
        }

        if (Ssse3.IsSupported)
        {
            var loTable = Vector128.LoadUnsafe(ref loBase, (nuint)(scalar * 16));
            var hiTable = Vector128.LoadUnsafe(ref hiBase, (nuint)(scalar * 16));
            var loMask = Vector128.Create((byte)0x0F);
            var hiMask = Vector128.Create((byte)0xF0);
            int end = length - 15;
            for (; i <= end; i += 16)
            {
                var v = Vector128.LoadUnsafe(ref symRef, (nuint)i);
                var lo = Ssse3.Shuffle(loTable, v & loMask);
                var hiNibbles = Sse2.ShiftRightLogical((v & hiMask).AsInt64(), 4).AsByte();
                var hi = Ssse3.Shuffle(hiTable, hiNibbles);
                Vector128.StoreUnsafe(lo ^ hi, ref symRef, (nuint)i);
            }
        }

        // Scalar fallback for any remaining bytes
        ref byte mulRow = ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(GF256.OctMul), scalar * 256);
        for (; i < length; i++)
            Unsafe.Add(ref symRef, i) =
                Unsafe.Add(ref mulRow, Unsafe.Add(ref symRef, i));
    }

    /// <summary>target[i] ^= GF256.Multiply(scalar, source[i]) for all i.</summary>
    internal static void XorScaleSymbolSimd(Span<byte> target, ReadOnlySpan<byte> source, byte scalar)
    {
        if (scalar == 0) return;
        if (scalar == 1) { XorSymbols(target, source); return; }

        ref byte tRef = ref MemoryMarshal.GetReference(target);
        ref byte sRef = ref MemoryMarshal.GetReference(source);
        ref byte loBase = ref MemoryMarshal.GetArrayDataReference(GF256.MulLow);
        ref byte hiBase = ref MemoryMarshal.GetArrayDataReference(GF256.MulHi);
        int length = target.Length;
        int i = 0;

        if (Avx2.IsSupported)
        {
            var lo128 = Vector128.LoadUnsafe(ref loBase, (nuint)(scalar * 16));
            var hi128 = Vector128.LoadUnsafe(ref hiBase, (nuint)(scalar * 16));
            var loTable = Vector256.Create(lo128, lo128);
            var hiTable = Vector256.Create(hi128, hi128);
            var loMask = Vector256.Create((byte)0x0F);
            var hiMask = Vector256.Create((byte)0xF0);
            int end = length - 31;
            for (; i <= end; i += 32)
            {
                var src = Vector256.LoadUnsafe(ref sRef, (nuint)i);
                var lo = Avx2.Shuffle(loTable, src & loMask);
                var hiNibbles = Avx2.ShiftRightLogical((src & hiMask).AsInt64(), 4).AsByte();
                var hi = Avx2.Shuffle(hiTable, hiNibbles);
                var mul = lo ^ hi;
                var dst = Vector256.LoadUnsafe(ref tRef, (nuint)i);
                Vector256.StoreUnsafe(dst ^ mul, ref tRef, (nuint)i);
            }
        }

        if (Ssse3.IsSupported)
        {
            var loTable = Vector128.LoadUnsafe(ref loBase, (nuint)(scalar * 16));
            var hiTable = Vector128.LoadUnsafe(ref hiBase, (nuint)(scalar * 16));
            var loMask = Vector128.Create((byte)0x0F);
            var hiMask = Vector128.Create((byte)0xF0);
            int end = length - 15;
            for (; i <= end; i += 16)
            {
                var src = Vector128.LoadUnsafe(ref sRef, (nuint)i);
                var lo = Ssse3.Shuffle(loTable, src & loMask);
                var hiNibbles = Sse2.ShiftRightLogical((src & hiMask).AsInt64(), 4).AsByte();
                var hi = Ssse3.Shuffle(hiTable, hiNibbles);
                var mul = lo ^ hi;
                var dst = Vector128.LoadUnsafe(ref tRef, (nuint)i);
                Vector128.StoreUnsafe(dst ^ mul, ref tRef, (nuint)i);
            }
        }

        // Scalar fallback
        ref byte mulRow = ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(GF256.OctMul), scalar * 256);
        for (; i < length; i++)
            Unsafe.Add(ref tRef, i) ^=
                Unsafe.Add(ref mulRow, Unsafe.Add(ref sRef, i));
    }
}