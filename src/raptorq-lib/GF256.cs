namespace Proxylity.RaptorQ;

/// <summary>
/// GF(2^8) arithmetic using the primitive polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D)
/// as specified by RFC 6330 §5.7.2, with OCT_EXP and OCT_LOG tables from §5.7.3/§5.7.4.
/// </summary>
public static class GF256
{
    // OCT_EXP: 510 entries (0..509) per RFC §5.7.3. The first 255 entries define alpha^i;
    // entries 255..509 repeat entries 0..254 to simplify modular index arithmetic.
    internal static ReadOnlySpan<byte> OctExp =>
    [
        1,2,4,8,16,32,64,128,29,58,116,232,205,135,19,38,76,152,45,90,180,117,234,201,143,3,6,12,24,48,96,192,157,39,78,
        156,37,74,148,53,106,212,181,119,238,193,159,35,70,140,5,10,20,40,80,160,93,186,105,210,185,111,222,161,95,190,97,
        194,153,47,94,188,101,202,137,15,30,60,120,240,253,231,211,187,107,214,177,127,254,225,223,163,91,182,113,226,217,
        175,67,134,17,34,68,136,13,26,52,104,208,189,103,206,129,31,62,124,248,237,199,147,59,118,236,197,151,51,102,204,
        133,23,46,92,184,109,218,169,79,158,33,66,132,21,42,84,168,77,154,41,82,164,85,170,73,146,57,114,228,213,183,115,
        230,209,191,99,198,145,63,126,252,229,215,179,123,246,241,255,227,219,171,75,150,49,98,196,149,55,110,220,165,87,
        174,65,130,25,50,100,200,141,7,14,28,56,112,224,221,167,83,166,81,162,89,178,121,242,249,239,195,155,43,86,172,69,
        138,9,18,36,72,144,61,122,244,245,247,243,251,235,203,139,11,22,44,88,176,125,250,233,207,131,27,54,108,216,173,71,
        142,
        // repeat (entries 255..509)
        1,2,4,8,16,32,64,128,29,58,116,232,205,135,19,38,76,152,45,90,180,117,234,201,143,3,6,12,24,48,96,192,157,39,78,
        156,37,74,148,53,106,212,181,119,238,193,159,35,70,140,5,10,20,40,80,160,93,186,105,210,185,111,222,161,95,190,97,
        194,153,47,94,188,101,202,137,15,30,60,120,240,253,231,211,187,107,214,177,127,254,225,223,163,91,182,113,226,217,
        175,67,134,17,34,68,136,13,26,52,104,208,189,103,206,129,31,62,124,248,237,199,147,59,118,236,197,151,51,102,204,
        133,23,46,92,184,109,218,169,79,158,33,66,132,21,42,84,168,77,154,41,82,164,85,170,73,146,57,114,228,213,183,115,
        230,209,191,99,198,145,63,126,252,229,215,179,123,246,241,255,227,219,171,75,150,49,98,196,149,55,110,220,165,87,
        174,65,130,25,50,100,200,141,7,14,28,56,112,224,221,167,83,166,81,162,89,178,121,242,249,239,195,155,43,86,172,69,
        138,9,18,36,72,144,61,122,244,245,247,243,251,235,203,139,11,22,44,88,176,125,250,233,207,131,27,54,108,216,173,71,
        142
    ];

    // OCT_LOG: 255 entries indexed 1..255 per RFC §5.7.4. Index 0 is excluded (log(0) undefined).
    // Stored as 256 entries with [0] = 0 as a sentinel; callers must never pass 0.
    internal static ReadOnlySpan<byte> OctLog =>
    [
        0, // [0] unused
        0,1,25,2,50,26,198,3,223,51,238,27,104,199,75,4,100,224,14,52,141,239,129,28,193,105,248,200,8,76,113,5,138,101,
        47,225,36,15,33,53,147,142,218,240,18,130,69,29,181,194,125,106,39,249,185,201,154,9,120,77,228,114,166,6,191,139,
        98,102,221,48,253,226,152,37,179,16,145,34,136,54,208,148,206,143,150,219,189,241,210,19,92,131,56,70,64,30,66,182,
        163,195,72,126,110,107,58,40,84,250,133,186,61,202,94,155,159,10,21,121,43,78,212,229,172,115,243,167,87,7,112,192,
        247,140,128,99,13,103,74,222,237,49,197,254,24,227,165,153,119,38,184,180,124,17,68,146,217,35,32,137,46,55,63,209,
        91,149,188,207,205,144,135,151,178,220,252,190,97,242,86,211,171,20,42,93,158,132,60,57,83,71,109,65,162,31,45,67,
        216,183,123,164,118,196,23,73,236,127,12,111,246,108,161,59,82,41,157,85,170,251,96,134,177,187,204,62,90,203,89,
        95,176,156,169,160,81,11,245,22,235,122,117,44,215,79,174,213,233,230,231,173,232,116,214,244,234,168,80,88,175
    ];

    /// <summary>Addition in GF(256) is XOR.</summary>
    public static byte Add(byte a, byte b) => (byte)(a ^ b);

    /// <summary>Multiplies two GF(256) elements using OCT_EXP/OCT_LOG per RFC §5.7.2.</summary>
    public static byte Multiply(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        return OctExp[OctLog[a] + OctLog[b]];
    }

    /// <summary>Divides two GF(256) elements using OCT_EXP/OCT_LOG per RFC §5.7.2.</summary>
    public static byte Divide(byte a, byte b)
    {
        if (b == 0) throw new DivideByZeroException();
        if (a == 0) return 0;
        return OctExp[OctLog[a] - OctLog[b] + 255];
    }

    /// <summary>Multiplicative inverse: OCT_EXP[255 - OCT_LOG[u]] per RFC §5.7.2.</summary>
    public static byte Inverse(byte a)
    {
        if (a == 0) throw new DivideByZeroException();
        return OctExp[255 - OctLog[a]];
    }

    /// <summary>alpha^i = OCT_EXP[i] for 0 &lt;= i &lt; 256 per RFC §5.7.2.</summary>
    public static byte AlphaPow(int i) => OctExp[i & 0xFF];

    // -----------------------------------------------------------------------
    // SIMD multiply tables (computed once at startup)
    // -----------------------------------------------------------------------

    /// <summary>Flat 256×256 GF(256) multiplication table.
    /// OctMul[scalar * 256 + value] = GF256.Multiply(scalar, value).</summary>
    internal static readonly byte[] OctMul = BuildOctMul();

    /// <summary>Low-nibble PSHUFB table.
    /// MulLow[scalar * 16 + (v &amp; 0x0F)] = GF256.Multiply(scalar, v &amp; 0x0F).</summary>
    internal static readonly byte[] MulLow = BuildMulLow();

    /// <summary>High-nibble PSHUFB table.
    /// MulHi[scalar * 16 + (v >> 4)] = GF256.Multiply(scalar, v &amp; 0xF0).</summary>
    internal static readonly byte[] MulHi = BuildMulHi();

    private static byte[] BuildOctMul()
    {
        var t = new byte[256 * 256];
        for (int s = 0; s < 256; s++)
            for (int v = 0; v < 256; v++)
                t[s * 256 + v] = Multiply((byte)s, (byte)v);
        return t;
    }

    private static byte[] BuildMulLow()
    {
        var t = new byte[256 * 16];
        for (int s = 0; s < 256; s++)
            for (int j = 0; j < 16; j++)
                t[s * 16 + j] = Multiply((byte)s, (byte)j);
        return t;
    }

    private static byte[] BuildMulHi()
    {
        var t = new byte[256 * 16];
        for (int s = 0; s < 256; s++)
            for (int j = 0; j < 16; j++)
                t[s * 16 + j] = Multiply((byte)s, (byte)(j << 4));
        return t;
    }
}
