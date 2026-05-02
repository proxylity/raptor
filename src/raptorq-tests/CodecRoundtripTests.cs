using Xunit;

namespace Proxylity.RaptorQ.Tests;

/// <summary>
/// End-to-end encode → decode roundtrip tests exercising the full RFC 6330 pipeline.
/// </summary>
public class CodecRoundtripTests
{
    private static byte[][] MakeSource(int K, int symbolSize, int seed = 0)
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

    [Theory]
    [InlineData(10,  64)]   // small K, small symbol
    [InlineData(10, 128)]
    [InlineData(50,  64)]
    [InlineData(100, 128)]
    [InlineData(200, 256)]
    [InlineData(788, 1331)] // realistic: 1MB object, 1331-byte symbols
    public void Systematic_Roundtrip_ExactK(int K, int symbolSize)
    {
        var source = MakeSource(K, symbolSize);
        var encoder = new RaptorQEncoder(source.Select(s => (ReadOnlyMemory<byte>)s).ToArray());

        // Collect exactly K systematic symbols (ESI 0..K-1)
        var received = new byte[K][];
        var esi      = new int[K];
        for (int i = 0; i < K; i++)
        {
            esi[i] = i;
            received[i] = encoder.GenerateSymbol(i).ToArray();
        }

        var decoder = new RaptorQDecoder(K, symbolSize);
        Assert.True(decoder.Decode(received, esi));

        var result = decoder.ReconstructOriginalObject(originalSize: (long)K * symbolSize);
        for (int i = 0; i < K; i++)
            Assert.Equal(source[i], result[(i * symbolSize)..((i + 1) * symbolSize)]);
    }

    [Theory]
    [InlineData(10,  64,  2)]   // 20% repair overhead
    [InlineData(50, 128,  5)]   // 10% repair overhead
    [InlineData(100, 256, 5)]
    [InlineData(788, 1331, 40)] // ~5% overhead
    public void Repair_Roundtrip_WithOverhead(int K, int symbolSize, int overhead)
    {
        var source = MakeSource(K, symbolSize, seed: 42);
        var encoder = new RaptorQEncoder(source.Select(s => (ReadOnlyMemory<byte>)s).ToArray());

        int total = K + overhead;
        // Use only repair symbols (ESI K .. K+overhead-1) – no systematic symbols
        var received = new byte[total][];
        var esi      = new int[total];
        for (int i = 0; i < total; i++)
        {
            esi[i] = K + i;   // pure repair
            received[i] = encoder.GenerateSymbol(K + i).ToArray();
        }

        var decoder = new RaptorQDecoder(K, symbolSize);
        Assert.True(decoder.Decode(received, esi));

        var result = decoder.ReconstructOriginalObject(originalSize: (long)K * symbolSize);
        for (int i = 0; i < K; i++)
            Assert.Equal(source[i], result[(i * symbolSize)..((i + 1) * symbolSize)]);
    }

    [Theory]
    [InlineData(10,  64)]
    [InlineData(100, 128)]
    [InlineData(788, 1331)]
    public void Mixed_Roundtrip_EvenEsiOnly(int K, int symbolSize)
    {
        // Receive only even ESIs (half systematic, half repair) – forces decoder
        // to substitute repair symbols into unused source slots.
        var source = MakeSource(K, symbolSize, seed: 7);
        var encoder = new RaptorQEncoder(source.Select(s => (ReadOnlyMemory<byte>)s).ToArray());

        // Pick K symbols: even ESIs from 0..(2K-1) gives exactly K symbols.
        var esiList = Enumerable.Range(0, K * 2).Where(e => e % 2 == 0).Take(K).ToArray();
        var received = esiList.Select(e => encoder.GenerateSymbol(e).ToArray()).ToArray();

        var decoder = new RaptorQDecoder(K, symbolSize);
        Assert.True(decoder.Decode(received, esiList));

        var result = decoder.ReconstructOriginalObject(originalSize: (long)K * symbolSize);
        for (int i = 0; i < K; i++)
            Assert.Equal(source[i], result[(i * symbolSize)..((i + 1) * symbolSize)]);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    [InlineData(1024)]
    public void K1_Roundtrip(int symbolSize)
    {
        // K=1: single source symbol. Trivial systematic path – no inactivation needed.
        var source = MakeSource(1, symbolSize, seed: 3);
        var encoder = new RaptorQEncoder(source.Select(s => (ReadOnlyMemory<byte>)s).ToArray());

        var received = new[] { encoder.GenerateSymbol(0).ToArray() };
        var esi      = new[] { 0 };

        var decoder = new RaptorQDecoder(1, symbolSize);
        Assert.True(decoder.Decode(received, esi));

        var result = decoder.ReconstructOriginalObject(originalSize: symbolSize);
        Assert.Equal(source[0], result);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(200)]
    public void SmallFile_SingleSymbol_Roundtrip(int fileSize)
    {
        // Mirrors the calculate_symbol_size fallback: file smaller than MIN_SYMBOL_SIZE
        // is sent as a single symbol (K=1) whose size equals the file length.
        var rng = new Random(99);
        var fileBytes = new byte[fileSize];
        rng.NextBytes(fileBytes);

        // K=1, symbolSize=fileSize (no padding needed)
        var source = new ReadOnlyMemory<byte>[] { fileBytes };
        var encoder = new RaptorQEncoder(source);

        var received = new[] { encoder.GenerateSymbol(0).ToArray() };
        var esi      = new[] { 0 };

        var decoder = new RaptorQDecoder(1, fileSize);
        Assert.True(decoder.Decode(received, esi));

        var result = decoder.ReconstructOriginalObject(originalSize: fileSize);
        Assert.Equal(fileBytes, result);
    }

    [Theory]
    [InlineData(2, 64,  128)]
    [InlineData(3, 32,  100)]
    [InlineData(4, 128, 256)]
    public void MultiBlock_Roundtrip(int numBlocks, int symbolSize, int K)
    {
        // Simulate a multi-block transfer: each block is encoded and decoded
        // independently, then the blocks are concatenated to recover the file.
        int blockDataSize = K * symbolSize;
        var rng = new Random(55);
        var fileBytes = new byte[numBlocks * blockDataSize];
        rng.NextBytes(fileBytes);

        var reconstructed = new byte[fileBytes.Length];

        for (int b = 0; b < numBlocks; b++)
        {
            int blockOffset = b * blockDataSize;
            var symbols = new ReadOnlyMemory<byte>[K];
            for (int i = 0; i < K; i++)
                symbols[i] = fileBytes.AsMemory(blockOffset + i * symbolSize, symbolSize);

            var encoder = new RaptorQEncoder(symbols);

            // Receive only repair symbols to exercise the full decoder path.
            int total = K + 5;
            var received = new byte[total][];
            var esi      = new int[total];
            for (int i = 0; i < total; i++)
            {
                esi[i] = K + i;
                received[i] = encoder.GenerateSymbol(K + i).ToArray();
            }

            var decoder = new RaptorQDecoder(K, symbolSize);
            Assert.True(decoder.Decode(received, esi));

            var blockResult = decoder.ReconstructOriginalObject(originalSize: blockDataSize);
            blockResult.CopyTo(reconstructed, blockOffset);
        }

        Assert.Equal(fileBytes, reconstructed);
    }

}
