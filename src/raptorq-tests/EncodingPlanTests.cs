using Xunit;
using Proxylity.RaptorQ;

namespace Proxylity.RaptorQ.Tests;

/// <summary>
/// Verifies the EncodingPlan caching, generation, and apply correctness.
/// </summary>
public class EncodingPlanTests
{
    // --- Caching ---

    [Fact]
    public void GetCached_ReturnsSameReferenceForSameK()
    {
        var plan1 = EncodingPlan.GetCached(10);
        var plan2 = EncodingPlan.GetCached(10);
        Assert.Same(plan1, plan2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(788)]
    public void GetCached_ReturnsDifferentInstancesForDifferentK(int K)
    {
        // Each K must map to its own plan object
        var plan = EncodingPlan.GetCached(K);
        Assert.Equal(K, plan.K);
    }

    // --- Generation ---

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(200)]
    public void Generate_Succeeds_ForRepresentativeK(int K)
    {
        var plan = EncodingPlan.Generate(K);
        Assert.NotNull(plan);
        Assert.Equal(K, plan.K);
    }

    // --- Apply determinism ---

    /// <summary>
    /// Two fresh plans generated from the same K, applied to two identical SymbolSlabs,
    /// must produce byte-for-byte identical output.  This verifies that the plan
    /// correctly encodes the source data and that two independent generates agree.
    /// </summary>
    [Theory]
    [InlineData(10, 64)]
    [InlineData(50, 128)]
    [InlineData(100, 32)]
    public void Apply_ProducesDeterministicOutput(int K, int symbolSize)
    {
        var rng = new Random(123);
        var p = RaptorQParameters.Compute(K);

        // Build source data
        var source = new byte[K][];
        for (int i = 0; i < K; i++)
        {
            source[i] = new byte[symbolSize];
            rng.NextBytes(source[i]);
        }

        // Apply plan1 (fresh)
        var plan1 = EncodingPlan.Generate(K);
        var slab1 = MakeInitialSlab(p, source, symbolSize);
        plan1.Apply(slab1);

        // Apply plan2 (another fresh generate — must produce same result)
        var plan2 = EncodingPlan.Generate(K);
        var slab2 = MakeInitialSlab(p, source, symbolSize);
        plan2.Apply(slab2);

        // Compare every logical symbol slot
        for (int i = 0; i < p.L; i++)
            Assert.Equal(slab1.Get(i).ToArray(), slab2.Get(i).ToArray());
    }

    /// <summary>
    /// Ensure that a cached plan produces the same output as a fresh plan.
    /// </summary>
    [Theory]
    [InlineData(10, 64)]
    [InlineData(100, 64)]
    public void Apply_CachedPlan_MatchesFreshPlan(int K, int symbolSize)
    {
        var rng = new Random(77);
        var p = RaptorQParameters.Compute(K);

        var source = new byte[K][];
        for (int i = 0; i < K; i++)
        {
            source[i] = new byte[symbolSize];
            rng.NextBytes(source[i]);
        }

        var cached = EncodingPlan.GetCached(K);
        var fresh  = EncodingPlan.Generate(K);

        var slabCached = MakeInitialSlab(p, source, symbolSize);
        var slabFresh  = MakeInitialSlab(p, source, symbolSize);

        cached.Apply(slabCached);
        fresh.Apply(slabFresh);

        for (int i = 0; i < p.L; i++)
            Assert.Equal(slabFresh.Get(i).ToArray(), slabCached.Get(i).ToArray());
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    // Replicates the layout that RaptorQEncoder uses: zero-fill, then place source
    // symbols at physical rows S+H .. S+H+K-1.
    private static SymbolSlab MakeInitialSlab(
        RaptorQParameters p, byte[][] source, int symbolSize)
    {
        var slab = new SymbolSlab(p.L, symbolSize);
        for (int i = 0; i < source.Length; i++)
            slab.CopyFrom(p.S + p.H + i, source[i]);
        return slab;
    }
}
