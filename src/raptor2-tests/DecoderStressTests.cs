using Proxylity.RaptorQ;
using Xunit;

namespace Raptor2.Tests;

/// <summary>The decoder must never report success with wrong data, at the exact shapes raptor2 uses.</summary>
public class DecoderStressTests
{
    [Theory]
    [InlineData(512, 1024, 1, 0.03)]
    [InlineData(512, 1024, 2, 0.03)]
    [InlineData(512, 1024, 3, 0.05)]
    [InlineData(512, 1024, 4, 0.10)]
    [InlineData(512, 1024, 5, 0.01)]
    [InlineData(410, 1280, 6, 0.03)]
    [InlineData(1, 1000, 7, 0.30)]
    [InlineData(3, 1000, 8, 0.30)]
    [InlineData(37, 512, 9, 0.20)]
    [InlineData(200, 1024, 10, 0.08)]
    [InlineData(512, 1024, 11, 0.06)]
    [InlineData(512, 1024, 12, 0.02)]
    public void Decode_with_losses_is_correct_or_reports_failure(int k, int t, int seed, double loss)
    {
        var rng = new Random(seed);
        for (int trial = 0; trial < 40; trial++)
        {
            var data = new byte[k * t];
            rng.NextBytes(data);
            var src = Enumerable.Range(0, k).Select(i => (ReadOnlyMemory<byte>)data.AsMemory(i * t, t)).ToArray();
            var enc = new RaptorQEncoder(src);
            var symbols = new List<byte[]>();
            var esis = new List<int>();
            for (int e = 0; e < k + Math.Max(60, k); e++)
            {
                if (rng.NextDouble() < loss) continue;
                symbols.Add(e < k ? data.AsSpan(e * t, t).ToArray() : enc.GenerateSymbol(e).ToArray());
                esis.Add(e);
            }
            // Try the way the engine does: with exactly K, K+1, ... received.
            for (int n = k; n <= symbols.Count; n++)
            {
                var dec = new RaptorQDecoder(k, t);
                if (!dec.Decode(symbols.Take(n).ToArray(), esis.Take(n).ToArray())) continue;
                var got = dec.ReconstructOriginalObject((long)k * t);
                Assert.True(data.AsSpan().SequenceEqual(got), $"trial {trial}: wrong data reported as decoded with {n} symbols, {k + Math.Max(60, k) - symbols.Count} lost");
                break;
            }
        }
    }
}
