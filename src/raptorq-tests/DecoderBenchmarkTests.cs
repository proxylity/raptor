using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Proxylity.RaptorQ.Tests;

public sealed class DecoderBenchmarkTests
{
    private const int SymbolSize = 1024;
    private const int TimedIterations = 3;

    private readonly ITestOutputHelper _output;

    public DecoderBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1000)]
    [InlineData(2000)]
    [InlineData(4000)]
    [Trait("Category", "Benchmark")]
    public void Decode_Benchmark_SystematicSymbols(int K)
    {
        byte[][] source = MakeSource(K, SymbolSize, seed: 1000 + K);
        var encoder = new RaptorQEncoder(source.Select(s => (ReadOnlyMemory<byte>)s).ToArray());

        var received = new byte[K][];
        var esi = new int[K];
        for (int i = 0; i < K; i++)
        {
            esi[i] = i;
            received[i] = encoder.GenerateSymbol(i).ToArray();
        }

        var p = RaptorQParameters.Compute(K);

        // Warm the JIT and verify correctness outside the timed iterations.
        var warmupDecoder = new RaptorQDecoder(K, SymbolSize);
        Assert.True(warmupDecoder.Decode(received, esi));

        byte[] warmupResult = warmupDecoder.ReconstructOriginalObject(originalSize: (long)K * SymbolSize);
        for (int i = 0; i < K; i++)
            Assert.Equal(source[i], warmupResult[(i * SymbolSize)..((i + 1) * SymbolSize)]);

        var elapsed = new TimeSpan[TimedIterations];
        for (int iteration = 0; iteration < TimedIterations; iteration++)
        {
            ForceFullCollection();

            var decoder = new RaptorQDecoder(K, SymbolSize);
            var sw = Stopwatch.StartNew();
            Assert.True(decoder.Decode(received, esi));
            sw.Stop();

            elapsed[iteration] = sw.Elapsed;
            _output.WriteLine($"K={K}, L={p.L}, symbolSize={SymbolSize}, iteration={iteration + 1}, decode={sw.Elapsed.TotalMilliseconds:F1} ms");
        }

        double minMs = elapsed.Min(t => t.TotalMilliseconds);
        double avgMs = elapsed.Average(t => t.TotalMilliseconds);
        double maxMs = elapsed.Max(t => t.TotalMilliseconds);
        _output.WriteLine($"K={K}, L={p.L}, symbolSize={SymbolSize}, min={minMs:F1} ms, avg={avgMs:F1} ms, max={maxMs:F1} ms");
    }

    [Theory]
    [InlineData(100, 4)]
    [InlineData(500, 8)]
    [InlineData(1000, 8)]
    [InlineData(2000, 16)]
    [InlineData(4000, 16)]
    [Trait("Category", "Benchmark")]
    public void Decode_Benchmark_FewMissingSystematicSymbols(int K, int missingCount)
    {
        byte[][] source = MakeSource(K, SymbolSize, seed: 2000 + K + missingCount);
        var encoder = new RaptorQEncoder(source.Select(s => (ReadOnlyMemory<byte>)s).ToArray());

        var received = new byte[K][];
        var esi = new int[K];

        int keepCount = K - missingCount;
        for (int i = 0; i < keepCount; i++)
        {
            esi[i] = i;
            received[i] = encoder.GenerateSymbol(i).ToArray();
        }

        for (int i = 0; i < missingCount; i++)
        {
            int repairEsi = K + i;
            esi[keepCount + i] = repairEsi;
            received[keepCount + i] = encoder.GenerateSymbol(repairEsi).ToArray();
        }

        var p = RaptorQParameters.Compute(K);

        var warmupDecoder = new RaptorQDecoder(K, SymbolSize);
        Assert.True(warmupDecoder.Decode(received, esi));

        byte[] warmupResult = warmupDecoder.ReconstructOriginalObject(originalSize: (long)K * SymbolSize);
        for (int i = 0; i < K; i++)
            Assert.Equal(source[i], warmupResult[(i * SymbolSize)..((i + 1) * SymbolSize)]);

        var elapsed = new TimeSpan[TimedIterations];
        for (int iteration = 0; iteration < TimedIterations; iteration++)
        {
            ForceFullCollection();

            var decoder = new RaptorQDecoder(K, SymbolSize);
            var sw = Stopwatch.StartNew();
            Assert.True(decoder.Decode(received, esi));
            sw.Stop();

            elapsed[iteration] = sw.Elapsed;
            _output.WriteLine($"K={K}, missing={missingCount}, L={p.L}, symbolSize={SymbolSize}, iteration={iteration + 1}, decode={sw.Elapsed.TotalMilliseconds:F1} ms");
        }

        double minMs = elapsed.Min(t => t.TotalMilliseconds);
        double avgMs = elapsed.Average(t => t.TotalMilliseconds);
        double maxMs = elapsed.Max(t => t.TotalMilliseconds);
        _output.WriteLine($"K={K}, missing={missingCount}, L={p.L}, symbolSize={SymbolSize}, min={minMs:F1} ms, avg={avgMs:F1} ms, max={maxMs:F1} ms");
    }

    [Theory]
    [InlineData(500, 64)]
    [InlineData(1000, 64)]
    [InlineData(2000, 64)]
    [InlineData(4000, 64)]
    [Trait("Category", "Benchmark")]
    public void Decode_Benchmark_BeliefPropagationPath_SystematicErasures(int K, int missingCount)
    {
        byte[][] source = MakeSource(K, SymbolSize, seed: 3000 + K + missingCount);
        var encoder = new RaptorQEncoder(source.Select(s => (ReadOnlyMemory<byte>)s).ToArray());

        var received = new byte[K][];
        var esi = new int[K];

        int keepCount = K - missingCount;
        for (int i = 0; i < keepCount; i++)
        {
            esi[i] = i;
            received[i] = encoder.GenerateSymbol(i).ToArray();
        }

        for (int i = 0; i < missingCount; i++)
        {
            int repairEsi = K + i;
            esi[keepCount + i] = repairEsi;
            received[keepCount + i] = encoder.GenerateSymbol(repairEsi).ToArray();
        }

        var p = RaptorQParameters.Compute(K);

        var warmupDecoder = new RaptorQDecoder(K, SymbolSize);
        Assert.True(warmupDecoder.Decode(received, esi));

        byte[] warmupResult = warmupDecoder.ReconstructOriginalObject(originalSize: (long)K * SymbolSize);
        for (int i = 0; i < K; i++)
            Assert.Equal(source[i], warmupResult[(i * SymbolSize)..((i + 1) * SymbolSize)]);

        var elapsed = new TimeSpan[TimedIterations];
        for (int iteration = 0; iteration < TimedIterations; iteration++)
        {
            ForceFullCollection();

            var decoder = new RaptorQDecoder(K, SymbolSize);
            var sw = Stopwatch.StartNew();
            Assert.True(decoder.Decode(received, esi));
            sw.Stop();

            elapsed[iteration] = sw.Elapsed;
            _output.WriteLine($"K={K}, missing={missingCount}, L={p.L}, symbolSize={SymbolSize}, iteration={iteration + 1}, decode={sw.Elapsed.TotalMilliseconds:F1} ms");
        }

        double minMs = elapsed.Min(t => t.TotalMilliseconds);
        double avgMs = elapsed.Average(t => t.TotalMilliseconds);
        double maxMs = elapsed.Max(t => t.TotalMilliseconds);
        _output.WriteLine($"K={K}, missing={missingCount}, L={p.L}, symbolSize={SymbolSize}, path=generic-bp, min={minMs:F1} ms, avg={avgMs:F1} ms, max={maxMs:F1} ms");
    }

    [Theory]
    [InlineData(500, 64)]
    [InlineData(1000, 128)]
    [InlineData(2000, 256)]
    [InlineData(4000, 512)]
    [Trait("Category", "Benchmark")]
    public void Decode_Benchmark_BeliefPropagationPath_RepairHeavyMix(int K, int systematicCount)
    {
        byte[][] source = MakeSource(K, SymbolSize, seed: 4000 + K + systematicCount);
        var encoder = new RaptorQEncoder(source.Select(s => (ReadOnlyMemory<byte>)s).ToArray());

        var received = new byte[K][];
        var esi = new int[K];

        for (int i = 0; i < systematicCount; i++)
        {
            esi[i] = i;
            received[i] = encoder.GenerateSymbol(i).ToArray();
        }

        int repairCount = K - systematicCount;
        for (int i = 0; i < repairCount; i++)
        {
            int repairEsi = K + i;
            esi[systematicCount + i] = repairEsi;
            received[systematicCount + i] = encoder.GenerateSymbol(repairEsi).ToArray();
        }

        var p = RaptorQParameters.Compute(K);

        var warmupDecoder = new RaptorQDecoder(K, SymbolSize);
        Assert.True(warmupDecoder.Decode(received, esi));

        byte[] warmupResult = warmupDecoder.ReconstructOriginalObject(originalSize: (long)K * SymbolSize);
        for (int i = 0; i < K; i++)
            Assert.Equal(source[i], warmupResult[(i * SymbolSize)..((i + 1) * SymbolSize)]);

        var elapsed = new TimeSpan[TimedIterations];
        for (int iteration = 0; iteration < TimedIterations; iteration++)
        {
            ForceFullCollection();

            var decoder = new RaptorQDecoder(K, SymbolSize);
            var sw = Stopwatch.StartNew();
            Assert.True(decoder.Decode(received, esi));
            sw.Stop();

            elapsed[iteration] = sw.Elapsed;
            _output.WriteLine($"K={K}, systematic={systematicCount}, repair={repairCount}, L={p.L}, symbolSize={SymbolSize}, iteration={iteration + 1}, decode={sw.Elapsed.TotalMilliseconds:F1} ms");
        }

        double minMs = elapsed.Min(t => t.TotalMilliseconds);
        double avgMs = elapsed.Average(t => t.TotalMilliseconds);
        double maxMs = elapsed.Max(t => t.TotalMilliseconds);
        _output.WriteLine($"K={K}, systematic={systematicCount}, repair={repairCount}, L={p.L}, symbolSize={SymbolSize}, path=generic-bp-repair-heavy, min={minMs:F1} ms, avg={avgMs:F1} ms, max={maxMs:F1} ms");
    }

    private static byte[][] MakeSource(int K, int symbolSize, int seed)
    {
        var rng = new Random(seed);
        var symbols = new byte[K][];
        for (int i = 0; i < K; i++)
        {
            symbols[i] = new byte[symbolSize];
            rng.NextBytes(symbols[i]);
        }

        return symbols;
    }

    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}