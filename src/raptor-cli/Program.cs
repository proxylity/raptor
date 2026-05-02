using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Proxylity.RaptorQ;

var cts = new CancellationTokenSource();
var config = parse_args(args);

const int MAX_BLOCK_BYTES = 512 * 1024;
const int MAX_UDP_SIZE = 1400;
const int MAX_SYMBOL_SIZE = 1280;
const int MIN_SYMBOL_SIZE = 256;
const int MAX_OBJECTID_BYTES = MAX_UDP_SIZE - MAX_SYMBOL_SIZE - RaptorQPacket.FIXED_SYMBOL_HEADER_LENGTH;

long BPS_LIMIT = (long)(config.RateMbps * 1_000_000.0);

var blocks_to_send = Channel.CreateBounded<(FileInfo fileInfo, string objectId, int blockIndex, int numBlocks, long blockSize, int symbolSize, int symbolCount)>(200);
var inflight = new ConcurrentDictionary<(string, int), (FileInfo fileInfo, int numBlocks, long lastSentMs, long blockSize, int symbolSize, int symbolCount, TaskCompletionSource tcs)>();

long totalBytesSent = 0;
var rateSw = Stopwatch.StartNew();

await log($"Sending to:  {config.Endpoint}", LogLevel.DETAIL); 
await log($"Rate limit:  {config.RateMbps} Mbps\n", LogLevel.DETAIL);

(var sender, var replies) = config.Protocol switch
{
    "udp" => udp_adapters(cts.Token),
    "wg" => wg_adapters(cts.Token),
    _ => throw new InvalidOperationException($"Unsupported protocol: {config.Protocol}")
};

var file_send_task = run(file_send_worker);
var block_send_task = run(block_send_worker);
var reply_task = run(reply_worker);

await block_send_task; // Wait for all blocks to be sent before allowing the program to exit, even if file_send_task completes first.
await log("All blocks sent and confirmations received (or timed-out)...", LogLevel.DETAIL);

cts.Cancel(); // signal the reply worker to exit

await reply_task; // Wait for all confirmations to be received before exiting.

await log($"Finished: {totalBytesSent} bytes sent in {rateSw.Elapsed.TotalSeconds:F2} seconds.", LogLevel.STANDARD);

return 0;


// ── Methods and Helpers ───────────────────────────────────────────────────────────────

async Task run(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (OperationCanceledException) { /* Normal cancellation path; do nothing. */ }
    catch (Exception ex)
    {
        await log($"ERROR: {ex.Message}\n{ex}", LogLevel.ERROR);
        Environment.Exit(10);
    }
}

async Task file_send_worker()
{
    var files_to_send = files();
    foreach (var file in files_to_send)
    {
        await queue_file_blocks(file);
    }
    blocks_to_send.Writer.Complete(); // Signal block send worker that no more blocks will be queued.
    await log("All files queued for sending.", LogLevel.DETAIL);
}

async Task block_send_worker()
{
    async Task child_worker(int i) {
        while (!cts.Token.IsCancellationRequested && await blocks_to_send.Reader.WaitToReadAsync(cts.Token))
        {
            await log($"Blocks waiting to send: {blocks_to_send.Reader.Count}...", LogLevel.DETAIL);
            while (blocks_to_send.Reader.TryRead(out var item))
            {
                var (fileInfo, objectId, blockIndex, numBlocks, blockSize, symbolSize, symbolCount) = item;
                await send_file_block(fileInfo, objectId, blockIndex, numBlocks, blockSize, symbolSize, symbolCount);
            }
        }
        await log($"Block send child worker {i} exiting.", LogLevel.DETAIL);
    }
    await Task.WhenAll(Enumerable.Range(0, 8).Select(i => child_worker(i)));

    await log("Block send workers exiting.", LogLevel.DETAIL);
}

async Task send_packet(RaptorQPacket packet)
{
    var buf = ArrayPool<byte>.Shared.Rent(MAX_UDP_SIZE);
    try
    {
        int n = packet.WriteTo(buf.AsSpan());
        await sender(buf.AsMemory(0, n));
        Interlocked.Add(ref totalBytesSent, n);
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buf);
    }
    // Rate-limit to BPS_LIMIT: delay if we are ahead of the target pace.
    if (BPS_LIMIT > 0) 
    {
        long targetMs = totalBytesSent * 8_000L / BPS_LIMIT;
        long actualMs = rateSw.ElapsedMilliseconds;
        if (targetMs > actualMs)
            await Task.Delay((int)(targetMs - actualMs));
    }
}

async Task reply_worker()
{
    if (config.ConfirmationLevel != ConfirmationLevel.NONE) {
        await log("Waiting for block confirmations...", LogLevel.DETAIL);
        await foreach (var reply in replies)
        {
            if (TransferStatePacket.TryFromBytes(reply, out var packet) && packet != null)
            {
                if (packet.NotificationType != TransferStatePacket.NotificationTypeEnum.PacketArrival) {
                    if (config.ConfirmationLevel.HasFlag((ConfirmationLevel)packet.NotificationType))
                    {
                        var objectid = packet.ObjectId;
                        if (inflight.TryGetValue((objectid, packet.BlockIndex), out var item))
                        {
                            if (!item.tcs.Task.IsCompleted) {
                                if (item.tcs.TrySetResult())
                                {
                                    await log($"Object {objectid}, block {packet.BlockIndex} confirmed.", LogLevel.DETAIL);
                                } else await log($"Warning: confirmation for object {objectid} block {packet.BlockIndex} received but no pending block found (possibly already timed out).", LogLevel.WARNING);
                            } // already completed
                        } // else await log($"Received confirmation for object {objectid} block {packet.BlockIndex} but no matching inflight block found.", LogLevel.WARNING);
                    } else await log($"Received confirmation with signal {packet.NotificationType} below configured level {config.ConfirmationLevel}, ignoring.", LogLevel.DETAIL);
                } await log($"Object {packet.ObjectId} block {packet.BlockIndex}: Server reports {packet.NotificationData} packets received.", LogLevel.DETAIL);
            } else await log($"Received invalid confirmation packet: {Encoding.UTF8.GetString(reply.Span)}", LogLevel.WARNING);
        }
    } else await log("Confirmation disabled; not listening for replies.", LogLevel.DETAIL);

    await log("Reply worker exiting.", LogLevel.DETAIL);
}

(Func<ReadOnlyMemory<byte>, Task> sender, IAsyncEnumerable<ReadOnlyMemory<byte>> replies) udp_adapters(CancellationToken token)
{
    var udp = new UdpClient();
    udp.Connect(config.Endpoint);
    static async IAsyncEnumerable<ReadOnlyMemory<byte>> receive_loop(UdpClient u, [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await u.ReceiveAsync(ct);
            yield return result.Buffer;
        }
        await Console.Out.WriteLineAsync("UDP receive loop exiting.");
    }
    
    return (async data =>
    {
        await udp.SendAsync(data, token);
    }, receive_loop(udp, token));
}

(Func<ReadOnlyMemory<byte>, Task> sender, IAsyncEnumerable<ReadOnlyMemory<byte>> replies) wg_adapters(CancellationToken token)
{
    var wg = new Proxylity.WireGuardClient.WireGuardClient(config.Endpoint, config.ServerKey, config.ClientKey);
    static async IAsyncEnumerable<ReadOnlyMemory<byte>> receive_loop(Proxylity.WireGuardClient.WireGuardClient wg, [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await wg.ReceiveAsync(ct);
            yield return result.Buffer;
        }
        await Console.Out.WriteLineAsync("WireGuard receive loop exiting.");
    }

    return (async data =>
    {
        await wg.SendAsync(data, token);
    }, receive_loop(wg, token));
}

IEnumerable<string> files()
{
    if (File.Exists(config.Path))
    {
        yield return config.Path;
    }
    else
    {
        var searchOption = config.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.EnumerateFiles(config.Path, "*", searchOption))
            yield return file;
    }
}

async Task queue_file_blocks(string filePath)
{
    var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), filePath)
        .Replace(Path.DirectorySeparatorChar, '/');
    var objectId = config.UsePrefix ? $"{config.Prefix}/{relativePath}" : relativePath;
    var fileInfo = new FileInfo(filePath);

    if (Encoding.UTF8.GetByteCount(objectId) > MAX_OBJECTID_BYTES)
    {
        await log($"ERROR: Object ID too long for UDP packet (max {MAX_OBJECTID_BYTES} bytes): {objectId}", LogLevel.ERROR);
        return;
    }

    int numBlocks = (int)Math.Ceiling((double)fileInfo.Length / MAX_BLOCK_BYTES);
    var blockSize = (fileInfo.Length + numBlocks - 1) / numBlocks;
    var (symbolSize, symbolCount) = calculate_symbol_size(blockSize);

    await log($"Queuing {numBlocks} block(s) of {blockSize} bytes for file: {relativePath}...", LogLevel.STANDARD);

    for (int blockIndex = 0; blockIndex < numBlocks && await blocks_to_send.Writer.WaitToWriteAsync(cts.Token); blockIndex++)
    {
        await blocks_to_send.Writer.WriteAsync((fileInfo, objectId, blockIndex, numBlocks, blockSize, (int)symbolSize, symbolCount));
    }
    await log("All blocks queued for sending.", LogLevel.DETAIL);
}

async Task send_file_block(FileInfo fileInfo, string objectId, int blockIndex, int numBlocks, long blockSize, int symbolSize, int symbolCount)
{
    await log($"Sending block {blockIndex} of {numBlocks} for object {objectId}...", LogLevel.DETAIL);
    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    inflight[(objectId, blockIndex)] = (fileInfo, numBlocks, rateSw.ElapsedMilliseconds, blockSize, symbolSize, symbolCount, tcs);

    long blockStart = (long)blockIndex * blockSize;
    long blockEnd = Math.Min(blockStart + blockSize, fileInfo.Length);
    long blockDataSize = blockEnd - blockStart;

    await log($"Symbol size: {symbolSize} bytes", LogLevel.DETAIL);
    await log($"Blocks:      {numBlocks}\n", LogLevel.DETAIL);

    int packetCount = (int)Math.Ceiling(symbolCount * (1.0 + config.Overhead));

    await log($"Block {blockIndex}/{numBlocks - 1}: K={symbolCount} T={packetCount}", LogLevel.DETAIL);

    // Copy this block's bytes into a padded buffer so every symbol slice is exactly
    // symbolSize bytes — the final symbol of the last block is zero-padded.
    int paddedSize = symbolCount * (int)symbolSize;
    var blockBuf = new byte[paddedSize];
    using var fileStream = File.OpenRead(fileInfo.FullName);
    fileStream.Seek(blockStart, SeekOrigin.Begin);
    await fileStream.ReadExactlyAsync(blockBuf.AsMemory(0, (int)blockDataSize), cts.Token);

    var sourceSymbols = new ReadOnlyMemory<byte>[symbolCount];
    for (int i = 0; i < symbolCount; i++)
        sourceSymbols[i] = new ReadOnlyMemory<byte>(blockBuf, (int)(i * symbolSize), (int)symbolSize);

    var encoder = new RaptorQEncoder(sourceSymbols);
    var encodeSw = Stopwatch.StartNew();
    var allSymbols = new Memory<byte>[packetCount];
    Parallel.For(0, packetCount, symbolId =>
    {
        allSymbols[symbolId] = encoder.GenerateSymbol(symbolId);
    });
    await log($"Encoded in {encodeSw.Elapsed.TotalSeconds:F2}s", LogLevel.DETAIL);

    // ── Send ─────────────────────────────────────────────────────────────────────
    int sent = 0;
    int step = Math.Max(100, packetCount / 20);
    for (int symbolId = 0; symbolId < packetCount && !tcs.Task.IsCompleted; symbolId++)
    {
        var packet = new RaptorQPacket
        {
            ObjectId = objectId,
            PacketIndex = symbolId,
            SymbolSize = (int)symbolSize,
            OriginalSize = fileStream.Length,
            BlockIndex = blockIndex,
            NumBlocks = numBlocks,
            Data = allSymbols[symbolId]
        };

        await send_packet(packet);
        sent++;
    }

    await log($"File {objectId} block {blockIndex}: {sent:N0} packets sent.", LogLevel.DETAIL);

    if (config.ConfirmationLevel != ConfirmationLevel.NONE && !tcs.Task.IsCompleted) {
        try {
            await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(config.BlockTimeoutMs), cts.Token);
        } catch (Exception e)
        {
            await log($"File {objectId} block {blockIndex} confirmation failed: {e.Message}", LogLevel.ERROR);
        } 
    }
    
    if (inflight.TryRemove((objectId, blockIndex), out _))
    {
    } else await log($"File {objectId} block {blockIndex}: Sent {sent:N0} packets, but block not found in inflight (possibly already confirmed or timed out).", LogLevel.ERROR);

    await log($"{blocks_to_send.Reader.Count} blocks still to send, {inflight.Count} blocks remain inflight", LogLevel.DETAIL);                                                
}

static (long size, int count) calculate_symbol_size(long fileSize)
{
    var kMin = (fileSize + MAX_SYMBOL_SIZE - 1) / MAX_SYMBOL_SIZE; // ceil(size / MAX_SYMBOL_SIZE)
    var kMax = fileSize / MIN_SYMBOL_SIZE;           // floor(size / MIN_SYMBOL_SIZE)

    if (kMin <= kMax)
    {
        // Smallest power of 2 >= kMin
        int k = kMin <= 1 ? 1 : 1 << (32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(kMin - 1)));
        if (k <= kMax)
            return (fileSize / k, k);
    }

    // Fallback: file is too small to fill even one MIN_SYMBOL_SIZE symbol.
    // Use a single symbol containing the whole file.
    return (fileSize, 1);
}

// ── Argument parsing and usage ─────────────────────────────────────────────────────

static void usage()
{
    Console.WriteLine(
@"
Usage:
 raptor <file> <udp|wg>://<ip>:<port> [--overhead <fraction>] [--rate-mbps <mbps>]

   <file>                   Path to the local file or folder to send.
   --recursive              If <file> is a folder, include all subfolders (default: false).
   [udp|wg]://<ip>:<port>   Proxylity UDP listener endpoint.
   --server-key <base64>    WireGuard public key of the server (required for wg://).
   --client-key <base64>    WireGuard private key of the client (required for wg://).
   --overhead               Fraction of repair symbols above K (default: 0.05 = 5%).
   --rate-mbps              Max send rate in Mbps (default: 10).
   --block-timeout          Seconds to wait for a block confirmation before retrying (default: 2, 0 = disabled).
   --confirm                Wait for confirmations from backend [NONE, ANY, PACKETS, BLOCK, FILE] (default: ANY = PACKETS | BLOCK | FILE).
   --verbose                Print detailed encoding and progress information.
   --silent                 Suppress all output except command errors.
   --no-prefix              Don't prefix object IDs with timestamps; use relative paths only.

 Examples:
   raptor myfile.zip udp://203.0.113.42:2048 --overhead 0.10 --rate-mbps 10
   raptor myfile.zip udp://203.0.113.42:2048 --block-timeout 5
   raptor myFolder wg://203.0.113.42:2048 --server-key <base64> --client-key <base64> --recursive --verbose
");
    Environment.Exit(1);
}

async Task log(string message, LogLevel level = LogLevel.STANDARD, [CallerMemberName] string? caller = null)
{
    if (level <= config.LogLevel)
    {
        Console.WriteLine($"[{DateTime.Now:s}] {message}");
    }
}

static Args parse_args(string[] args)
{
    if (args.Length < 2)
    {
        usage();
    }

    string path = args[0];
    string udpUri = args[1];
    string protocol = udpUri.Split(':')[0].ToLower();
    ReadOnlyMemory<byte> serverKey = ReadOnlyMemory<byte>.Empty;
    ReadOnlyMemory<byte> clientKey = ReadOnlyMemory<byte>.Empty;
    double overhead = 0.05;
    double rateMbps = 10.0;
    long blockTimeoutMs = 2000; // ms, set to 0 to disable retries. only valid when --no-confirm is not set
    bool recursive = false;
    ConfirmationLevel confirmationLevel = ConfirmationLevel.ANY;
    LogLevel logLevel = LogLevel.STANDARD;
    bool usePrefix = true;
    string prefix = $"{DateTime.UtcNow:yyyyMMddHHmmss}";

    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--server-key" && args.Length > i + 1 && args[i + 1] is string serverKeyBase64)
        {
            serverKey = Convert.FromBase64String(serverKeyBase64);
            i++;
        }
        else if (args[i] == "--client-key" && args.Length > i + 1 && args[i + 1] is string clientKeyBase64)
        {
            clientKey = Convert.FromBase64String(clientKeyBase64);
            i++;
        }
        else if (args[i] == "--overhead" && args.Length > i + 1 && double.TryParse(args[i + 1], out var oh))
        {
            overhead = oh;
            i++;
        }
        else if (args[i] == "--rate-mbps" && args.Length > i + 1 && double.TryParse(args[i + 1], out var rm))
        {
            rateMbps = rm;
            i++;
        }
        else if (args[i] == "--block-timeout" && args.Length > i + 1 && double.TryParse(args[i + 1], out var bt))
        {
            blockTimeoutMs = (long)(bt * 1000);
            i++;
        }
        else if (args[i] == "--confirm" && args.Length > i + 1 && Enum.TryParse<ConfirmationLevel>(args[i + 1], true, out var cl))
        {
            confirmationLevel = cl;
            i++;
        }
        else if (args[i] == "--recursive")
        {
            recursive = true;
        }
        else if (args[i] == "--verbose")
        {
            logLevel = LogLevel.DETAIL;
        }
        else if (args[i] == "--silent")
        {
            logLevel = LogLevel.SILENT;
        }
        else if (args[i] == "--no-prefix") {
            usePrefix = false;
        }
        else if (args[i] == "--prefix" && args.Length > i + 1 && args[i + 1] is string p)
        {
            prefix = p;
            i++;
        }
        else usage();
    }

    if (!File.Exists(path) && !Directory.Exists(path))
    {
        Console.Error.WriteLine($"\nERROR: Path not found: {path}");
        Environment.Exit(2);
    }

    if (protocol == "wg" && (serverKey.IsEmpty || clientKey.IsEmpty))
    {
        Console.Error.WriteLine("\nERROR: WireGuard mode requires --server-key and --client-key.");
        Environment.Exit(3);
    }

    if (protocol == "wg" && (serverKey.Length != 32 || clientKey.Length != 32))
    {
        Console.Error.WriteLine("\nERROR: Invalid WireGuard keys: must be 32 bytes after base64 decoding.");
        Environment.Exit(4);
    }

    var uriParts = udpUri.Split([':', '/'], StringSplitOptions.RemoveEmptyEntries);
    if (uriParts.Length != 3)
    {
        Console.Error.WriteLine($"\nERROR: Invalid endpoint URI: {udpUri}");
        Environment.Exit(5);
    }

    if (!int.TryParse(uriParts[2], out var port) || port < 1 || port > 65535)
    {
        Console.Error.WriteLine($"\nERROR: Invalid port number in URI: {uriParts[2]}");
        Environment.Exit(6);
    }

    return new Args(
        Path: path,
        ServerKey: serverKey,
        ClientKey: clientKey,
        Overhead: overhead,
        RateMbps: rateMbps,
        Recursive: recursive,
        ConfirmationLevel: confirmationLevel,
        LogLevel: logLevel,
        BlockTimeoutMs: blockTimeoutMs,
        UsePrefix: usePrefix,
        Prefix: prefix,
        Host: uriParts[1],
        Port: port,
        Protocol: protocol,
        Endpoint: new IPEndPoint(Dns.GetHostAddresses(uriParts[1])[0], port)
    );
}

internal record Args (
    string Path,
    ReadOnlyMemory<byte> ServerKey,
    ReadOnlyMemory<byte> ClientKey,
    double Overhead,
    double RateMbps,
    bool Recursive,
    ConfirmationLevel ConfirmationLevel,
    LogLevel LogLevel,
    long BlockTimeoutMs,
    bool UsePrefix,
    string Prefix,
    IPEndPoint Endpoint,
    string Host,
    int Port,
    string Protocol
);

[Flags] enum ConfirmationLevel : byte
{
    ANY = 0xFF,
    NONE = 0,
    PACKETS = 0x1,
    BLOCK = 0x2,
    FILE = 0x4
}

enum LogLevel
{
    SILENT = 0,
    ERROR = 1,
    STANDARD = 2,
    WARNING = 3,
    DETAIL = 4
}