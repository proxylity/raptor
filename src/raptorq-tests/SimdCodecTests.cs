using Xunit;
using Proxylity.RaptorQ;

namespace Proxylity.RaptorQ.Tests;

/// <summary>
/// Verifies the SIMD GF(256) multiply helpers in RaptorQCodec against the scalar
/// reference implementation in GF256.Multiply.  Three symbol sizes exercise the
/// AVX2 main loop, the SSSE3 tail, and the scalar byte-level fallback together.
/// </summary>
public class SimdCodecTests
{
    // Symbol sizes chosen to cover different SIMD tail cases:
    //   41  – non-multiple of 32 → AVX2 main loop + SSSE3/scalar tail
    //   48  – multiple of 16, not 32 → SSSE3 lane, no scalar tail
    //  256  – multiple of 32 → AVX2 main loop only, no tail
    [Theory]
    [InlineData(41)]
    [InlineData(48)]
    [InlineData(256)]
    public void ScaleSymbolSimd_MatchesScalarMultiply(int symbolSize)
    {
        var rng = new Random(42);
        for (int scalar = 0; scalar < 256; scalar++)
        {
            var data = new byte[symbolSize];
            rng.NextBytes(data);

            // Compute expected result via scalar GF256
            var expected = new byte[symbolSize];
            for (int j = 0; j < symbolSize; j++)
                expected[j] = GF256.Multiply((byte)scalar, data[j]);

            RaptorQCodec.ScaleSymbolSimd(data, (byte)scalar);

            Assert.Equal(expected, data);
        }
    }

    [Theory]
    [InlineData(41)]
    [InlineData(48)]
    [InlineData(256)]
    public void XorScaleSymbolSimd_MatchesScalarFMA(int symbolSize)
    {
        var rng = new Random(99);
        // Test all scalars to cover 0, 1 (special-cased), and the SIMD path
        for (int scalar = 0; scalar < 256; scalar++)
        {
            var target = new byte[symbolSize];
            var source = new byte[symbolSize];
            rng.NextBytes(target);
            rng.NextBytes(source);

            var expected = new byte[symbolSize];
            for (int j = 0; j < symbolSize; j++)
                expected[j] = (byte)(target[j] ^ GF256.Multiply((byte)scalar, source[j]));

            RaptorQCodec.XorScaleSymbolSimd(target, source, (byte)scalar);

            Assert.Equal(expected, target);
        }
    }

    /// <summary>Scale by 0 clears the symbol.</summary>
    [Fact]
    public void ScaleSymbolSimd_ByZero_ClearsSymbol()
    {
        var data = Enumerable.Range(1, 64).Select(i => (byte)i).ToArray();
        RaptorQCodec.ScaleSymbolSimd(data, 0);
        Assert.All(data, b => Assert.Equal(0, b));
    }

    /// <summary>Scale by 1 is a no-op.</summary>
    [Fact]
    public void ScaleSymbolSimd_ByOne_IsIdentity()
    {
        var rng = new Random(7);
        var data = new byte[64];
        rng.NextBytes(data);
        var original = data.ToArray();
        RaptorQCodec.ScaleSymbolSimd(data, 1);
        Assert.Equal(original, data);
    }

    /// <summary>XorScale with scalar=0 is a no-op on the target.</summary>
    [Fact]
    public void XorScaleSymbolSimd_ByZero_LeavesTargetUnchanged()
    {
        var rng = new Random(5);
        var target = new byte[64];
        var source = new byte[64];
        rng.NextBytes(target);
        rng.NextBytes(source);
        var original = target.ToArray();
        RaptorQCodec.XorScaleSymbolSimd(target, source, 0);
        Assert.Equal(original, target);
    }

    /// <summary>XorScale with scalar=1 equals plain XOR.</summary>
    [Fact]
    public void XorScaleSymbolSimd_ByOne_EqualsXor()
    {
        var rng = new Random(3);
        var target = new byte[64];
        var source = new byte[64];
        rng.NextBytes(target);
        rng.NextBytes(source);
        var expected = target.Zip(source, (t, s) => (byte)(t ^ s)).ToArray();
        RaptorQCodec.XorScaleSymbolSimd(target, source, 1);
        Assert.Equal(expected, target);
    }
}
