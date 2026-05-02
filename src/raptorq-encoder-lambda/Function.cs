using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.S3;
using Amazon.S3.Model;
using Proxylity.RaptorQ;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;

var instance = new Function();
var serializer = new SourceGeneratorLambdaJsonSerializer<JsonContext>();
await LambdaBootstrapBuilder.Create<Payload, Task<Response>>(instance.FunctionHandler, serializer)
    .Build()
    .RunAsync();

public class Function
{
    private readonly IAmazonS3 _s3Client;

    // Maximum UDP payload size.
    private const int MAX_UDP_SIZE    = 1400;
    private const int MIN_SYMBOL_SIZE  = 256;
    private const int MAX_SYMBOL_SIZE  = 1280;

    /// <summary>
    /// Maximum source bytes per RaptorQ source block (512 KiB).  Keeps K ≤ ~392 at the
    /// maximum symbol size of 1 339 bytes so encoding and plan-generation complete well
    /// within Lambda time budgets.  S3 multipart part accumulation (minimum 5 MiB per
    /// non-last part) is handled by the block completer, which groups multiple decoded
    /// blocks into each S3 part.  If this value changes, update BlocksPerPart in the
    /// block completer accordingly.
    /// </summary>
    private const int MAX_BLOCK_BYTES = 512 * 1024;

    private static readonly double RepairOverhead =
        double.TryParse(Environment.GetEnvironmentVariable("REPAIR_OVERHEAD"), out var oh)
            ? oh : 0.05;

    public Function()
    {
        _s3Client = new AmazonS3Client();
    }

    public async Task<Response> FunctionHandler(Payload payload, ILambdaContext context)
    {
        var uri      = new Amazon.S3.Util.AmazonS3Uri(payload.S3Uri);
        var metadata = await _s3Client.GetObjectMetadataAsync(uri.Bucket, uri.Key);
        var fileSize = metadata.ContentLength;

        // Symbol size is fixed across all blocks so the decoder can reconstruct boundaries.
        // Compute against MAX_BLOCK_BYTES so all blocks use the same symbol size.
        int symbolSize = CalculateSymbolSize(Math.Min((long)MAX_BLOCK_BYTES, fileSize), System.Text.Encoding.UTF8.GetByteCount(uri.Key));

        // Number of source blocks required.  Each block holds at most MAX_BLOCK_BYTES bytes.
        // Using ceil-division guarantees every non-last block is exactly MAX_BLOCK_BYTES,
        // satisfying S3's requirement that all parts except the final one are >= 5 MiB.
        long maxBlockBytes = MAX_BLOCK_BYTES;
        int  numBlocks     = (int)Math.Ceiling((double)fileSize / maxBlockBytes);

        var objectId      = uri.Key;
        int totalSymbols  = 0;

        context.Logger.LogInformation(
            $"Encoding s3://{uri.Bucket}/{uri.Key}: fileSize={fileSize} symbolSize={symbolSize} " +
            $"numBlocks={numBlocks} objectId={objectId}");

        // Pacing: bytes per millisecond budget (0 = unlimited).
        double bytesPerMs = payload.RateMbps > 0
            ? payload.RateMbps * 1_000_000.0 / 8.0 / 1000.0
            : 0;

        using var udpClient = new UdpClient();
        // Resolve the hostname at send time; Proxylity listeners are addressed by DNS name.
        var addresses = await Dns.GetHostAddressesAsync(payload.Destination.Host);
        var endpoint  = new IPEndPoint(addresses[0], payload.Destination.Port);

        for (int blockIndex = 0; blockIndex < numBlocks; blockIndex++)
        {
            long blockStart    = (long)blockIndex * maxBlockBytes;
            long blockEnd      = Math.Min(blockStart + maxBlockBytes, fileSize);
            long blockDataSize = blockEnd - blockStart;

            // Download only this block's bytes via an S3 Range request to avoid
            // loading the full file into memory for multi-gigabyte transfers.
            var blockData = await DownloadS3Range(uri.Bucket, uri.Key, blockStart, blockEnd - 1);

            int K = (int)Math.Ceiling((double)blockDataSize / symbolSize);
            int T = (int)Math.Ceiling(K * (1.0 + RepairOverhead));

            var sourceSymbols = PrepareSourceSymbols(blockData, K, symbolSize);
            var encoder = new RaptorQEncoder(sourceSymbols);

            context.Logger.LogInformation(
                $"  Block {blockIndex}/{numBlocks - 1}: K={K} T={T} blockDataSize={blockDataSize}");

            var encodeSw   = Stopwatch.StartNew();
            var allSymbols = new Memory<byte>[T];
            Parallel.For(0, T, symbolId =>
            {
                allSymbols[symbolId] = encoder.GenerateSymbol(symbolId);
            });
            context.Logger.LogInformation(
                $"    Encoded in {encodeSw.Elapsed.TotalSeconds:F2}s");

            // Rate-limited send: rent a pooled buffer per packet to avoid per-send allocation.
            var blockSw = Stopwatch.StartNew();
            long bytesSentInBlock = 0;

            for (int symbolId = 0; symbolId < T; symbolId++)
            {
                var packet = new RaptorQPacket
                {
                    ObjectId      = objectId,
                    PacketIndex   = symbolId,
                    SymbolSize    = symbolSize,
                    OriginalSize  = fileSize,
                    BlockIndex    = blockIndex,
                    NumBlocks     = numBlocks,
                    Data          = allSymbols[symbolId]
                };

                var buf = ArrayPool<byte>.Shared.Rent(MAX_UDP_SIZE);
                try
                {
                    int n = packet.WriteTo(buf.AsSpan(0, MAX_UDP_SIZE));
                    await udpClient.SendAsync(buf.AsMemory(0, n), endpoint);
                    totalSymbols++;
                    bytesSentInBlock += n;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }

                if (bytesPerMs > 0)
                {
                    // Stopwatch is TSC-based — far more precise than DateTime.UtcNow.
                    double expectedMs = bytesSentInBlock / bytesPerMs;
                    double actualMs   = blockSw.Elapsed.TotalMilliseconds;
                    int delayMs       = (int)(expectedMs - actualMs);
                    if (delayMs > 0)
                        await Task.Delay(delayMs);
                }
            }
        }

        return new Response
        {
            ObjectId     = objectId,
            NumBlocks    = numBlocks,
            TotalSymbols = totalSymbols,
            SymbolSize   = symbolSize,
            OriginalSize = fileSize
        };
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ReadOnlyMemory<byte>[] PrepareSourceSymbols(byte[] blockData, int K, int symbolSize)
    {
        // Pad to an exact multiple of SymbolSize once so every slice is exactly SymbolSize bytes.
        int paddedSize = K * symbolSize;
        if (blockData.Length < paddedSize)
            Array.Resize(ref blockData, paddedSize);

        var symbols = new ReadOnlyMemory<byte>[K];
        for (int i = 0; i < K; i++)
            symbols[i] = new ReadOnlyMemory<byte>(blockData, i * symbolSize, symbolSize);
        return symbols;
    }

    private async Task<byte[]> DownloadS3Range(string bucket, string key, long fromByte, long toByte)
    {
        var request = new GetObjectRequest
        {
            BucketName = bucket,
            Key        = key,
            ByteRange  = new ByteRange(fromByte, toByte)
        };
        // Pre-size the buffer from the known byte range — avoids the MemoryStream.ToArray() copy.
        var buffer = new byte[toByte - fromByte + 1];
        using var response = await _s3Client.GetObjectAsync(request);
        await response.ResponseStream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static int CalculateSymbolSize(long blockDataSize, int objectIdBytes)
    {
        // Max symbol size is constrained by what fits in a single UDP datagram after the header.
        // FIXED_SYMBOL_HEADER_LENGTH = 1 (id_length) + 8 (CommonOTI) + 4 (SchemeOTI) + 4 (FECPayloadID)
        int maxSymbolSize = Math.Min(MAX_SYMBOL_SIZE, MAX_UDP_SIZE - RaptorQPacket.FIXED_SYMBOL_HEADER_LENGTH - objectIdBytes);
        int minSymbolSize = MIN_SYMBOL_SIZE;

        // Smallest power-of-2 K in [kMin, kMax], matching the CLI's calculate_symbol_size logic.
        long kMin = (blockDataSize + maxSymbolSize - 1) / maxSymbolSize;
        long kMax = blockDataSize / minSymbolSize;

        if (kMin <= kMax)
        {
            int k = kMin <= 1 ? 1 : 1 << (32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(kMin - 1)));
            if (k <= kMax)
                return (int)(blockDataSize / k);
        }

        // Fallback: block is smaller than minSymbolSize — use a single symbol.
        return (int)Math.Min(blockDataSize, maxSymbolSize);
    }
}

public class Payload
{
    public string S3Uri { get; set; } = string.Empty;
    public Destination Destination { get; set; } = new();
    /// <summary>
    /// Target send rate in Mbps.  0 (default) means unlimited.
    /// </summary>
    public double RateMbps { get; set; } = 0;
}

public class Destination
{
    /// <summary>Hostname or IP address of the UDP listener endpoint.</summary>
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
}

public class Response
{
    public string ObjectId { get; set; } = string.Empty;
    public int NumBlocks { get; set; }
    public int TotalSymbols { get; set; }
    public int SymbolSize { get; set; }
    public long OriginalSize { get; set; }
}

[JsonSerializable(typeof(Payload))]
[JsonSerializable(typeof(Destination))]
[JsonSerializable(typeof(Response))]
public partial class JsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
