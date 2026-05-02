using Xunit;
using Proxylity.RaptorQ;

namespace Proxylity.RaptorQ.Tests;

/// <summary>
/// Verifies GF(256) arithmetic against spot-checks derived from RFC 6330 §5.7 tables.
/// The OCT_EXP/OCT_LOG tables define the field; these tests confirm internal consistency
/// and a subset of values from those tables.
/// </summary>
public class GF256Tests
{
    // --- OCT_EXP spot-checks per RFC §5.7.3 ---

    [Theory]
    [InlineData(0, 1)]    // alpha^0 = 1
    [InlineData(1, 2)]    // alpha^1 = 2
    [InlineData(2, 4)]    // alpha^2 = 4
    [InlineData(7, 128)]  // alpha^7 = 128
    [InlineData(8, 29)]   // alpha^8 = 29  (first non-trivial: 256 mod 0x11D = 29)
    [InlineData(254, 142)]// alpha^254 per RFC table
    public void AlphaPow_MatchesRfcTable(int i, byte expected)
        => Assert.Equal(expected, GF256.AlphaPow(i));

    // --- Multiply ---

    [Fact]
    public void Multiply_ByZero_IsZero()
    {
        for (int a = 0; a < 256; a++)
        {
            Assert.Equal(0, GF256.Multiply((byte)a, 0));
            Assert.Equal(0, GF256.Multiply(0, (byte)a));
        }
    }

    [Fact]
    public void Multiply_ByOne_IsIdentity()
    {
        for (int a = 1; a < 256; a++)
            Assert.Equal((byte)a, GF256.Multiply((byte)a, 1));
    }

    [Fact]
    public void Multiply_IsCommutative()
    {
        // Sample 1000 pairs
        for (int a = 1; a < 256; a += 3)
            for (int b = 1; b < 256; b += 5)
                Assert.Equal(GF256.Multiply((byte)a, (byte)b),
                             GF256.Multiply((byte)b, (byte)a));
    }

    [Fact]
    public void Multiply_Alpha1_By_Alpha1_Equals_Alpha2()
        // 2 * 2 = alpha^1 * alpha^1 = alpha^2 = 4
        => Assert.Equal(4, GF256.Multiply(2, 2));

    [Fact]
    public void Multiply_Alpha8Squared()
    {
        // alpha^8 = 29; alpha^16 = alpha^(8+8) => OctExp[16]
        byte a8 = GF256.AlphaPow(8);   // 29
        byte a16 = GF256.AlphaPow(16); // 45
        Assert.Equal(a16, GF256.Multiply(a8, a8));
    }

    // --- Divide ---

    [Fact]
    public void Divide_ByZero_Throws()
        => Assert.Throws<DivideByZeroException>(() => GF256.Divide(1, 0));

    [Fact]
    public void Divide_SelfBySelf_IsOne()
    {
        for (int a = 1; a < 256; a++)
            Assert.Equal(1, GF256.Divide((byte)a, (byte)a));
    }

    [Fact]
    public void Divide_IsInverseOfMultiply()
    {
        for (int a = 1; a < 256; a += 7)
            for (int b = 1; b < 256; b += 11)
            {
                var product = GF256.Multiply((byte)a, (byte)b);
                Assert.Equal((byte)a, GF256.Divide(product, (byte)b));
            }
    }

    // --- Inverse ---

    [Fact]
    public void Inverse_OfZero_Throws()
        => Assert.Throws<DivideByZeroException>(() => GF256.Inverse(0));

    [Fact]
    public void Inverse_MultiplyGivesOne()
    {
        for (int a = 1; a < 256; a++)
        {
            var inv = GF256.Inverse((byte)a);
            Assert.Equal(1, GF256.Multiply((byte)a, inv));
        }
    }

    [Fact]
    public void Inverse_OfOne_IsOne()
        => Assert.Equal(1, GF256.Inverse(1));

    // --- Add (XOR) ---

    [Fact]
    public void Add_IsXor()
    {
        for (int a = 0; a < 256; a++)
            for (int b = 0; b < 256; b++)
                Assert.Equal((byte)(a ^ b), GF256.Add((byte)a, (byte)b));
    }

    [Fact]
    public void Add_SelfIsZero()
    {
        for (int a = 0; a < 256; a++)
            Assert.Equal(0, GF256.Add((byte)a, (byte)a));
    }
}
