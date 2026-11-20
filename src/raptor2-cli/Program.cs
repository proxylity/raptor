using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Raptor2.Cli;
using Raptor2.Wire;

var options = Args.Parse(args);
if (options is null) return 1;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

ITransport transport = options.Protocol == "wg"
    ? new WireGuardTransport(options.Endpoint, options.ServerKey, options.ClientKey)
    : new UdpTransport(options.Endpoint);

var send = new SendOptions
{
    Overhead = options.Overhead,
    RateMbps = options.RateMbps,
    Lanes = options.Lanes,
    SmallSlots = options.SmallSlots,
    LargeSlots = options.LargeSlots,
    MaxBlockTimeout = TimeSpan.FromSeconds(options.BlockTimeoutSeconds),
    NoProgressTimeout = TimeSpan.FromSeconds(options.NoProgressSeconds),
    WaitForCompletion = options.Confirm,
    Log = options.Level >= 1 ? s => Console.Error.WriteLine(s) : null,
    Detail = options.Level >= 3 ? s => Console.Error.WriteLine($"[{DateTime.Now:T}] {s}") : null,
};
var sender = new Sender(transport, send, RandomNumberGenerator.GetBytes(8));

// raptor2 is for small files: up to 1 MiB is one or two blocks, confirmed in a single round trip. Larger files are
// usually faster and cheaper through S3 directly (aws s3 cp), so by default they are refused rather than sent slowly.
// --max-size raises the limit; files over 16 MiB then go as multipart uploads across the large-file lanes.
long MaxFileBytes = options.MaxSize;
var tooBig = Args.Files(options).Select(f => new FileInfo(f)).Where(f => f.Length > MaxFileBytes).ToList();
if (tooBig.Count > 0)
{
    foreach (var f in tooBig.Take(10)) Console.Error.WriteLine($"ERROR: {f.FullName} is {f.Length:N0} bytes");
    if (tooBig.Count > 10) Console.Error.WriteLine($"ERROR: ... and {tooBig.Count - 10} more");
    Console.Error.WriteLine($"The limit is {MaxFileBytes:N0} bytes (--max-size). Use aws s3 cp for large files, or raise --max-size.");
    return 2;
}

long total = 0;
var cwd = Directory.GetCurrentDirectory();
foreach (var file in Args.Files(options))
{
    var info = new FileInfo(file);
    var full = info.FullName;
    var baseDir = full.StartsWith(cwd + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        ? cwd
        : Path.GetDirectoryName(Path.GetFullPath(options.Path.TrimEnd('/', '\\')))!;
    var relative = Path.GetRelativePath(baseDir, full).Replace(Path.DirectorySeparatorChar, '/');
    var key = options.UsePrefix ? $"{options.Prefix}/{relative}" : relative;
    if (Encoding.UTF8.GetByteCount(key) > Layout.MaxKeyBytes)
    {
        Console.Error.WriteLine($"ERROR: object key too long (max {Layout.MaxKeyBytes} bytes): {key}");
        return 2;
    }
    sender.AddObject(key, full, info.Length);
    total += info.Length;
}
if (sender.Objects.Count == 0) { Console.Error.WriteLine("No files to send."); return 2; }

var clock = Stopwatch.StartNew();
bool ok = await sender.RunAsync(cts.Token);
clock.Stop();

if (options.Level >= 2)
{
    var s = sender.Stats;
    double secs = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
    Console.WriteLine($"{(ok ? "Finished" : "FAILED")}: {sender.Objects.Count} file(s), {total:N0} bytes in {secs:F2} s " +
        $"({total * 8 / secs / 1e6:F1} Mbps goodput); {s.Packets:N0} packets, {s.Bytes:N0} bytes on the wire, " +
        $"{s.RepairSymbols:N0} up-front repair symbols, {s.Retries:N0} repair pass(es), {s.Replies:N0} replies" +
        (sender.Repairs.Adaptive ? $", loss estimate {sender.Repairs.LossEstimate:P3}." : "."));
}
return ok ? 0 : 3;

internal sealed record Args(
    string Path, string Protocol, IPEndPoint Endpoint, ReadOnlyMemory<byte> ServerKey, ReadOnlyMemory<byte> ClientKey,
    double? Overhead, double RateMbps, int Lanes, double BlockTimeoutSeconds, double NoProgressSeconds,
    bool Recursive, bool Confirm, int Level, bool UsePrefix, string Prefix, int SmallSlots, int LargeSlots, long MaxSize)
{
    public static IEnumerable<string> Files(Args a)
    {
        if (File.Exists(a.Path)) { yield return a.Path; yield break; }
        var option = a.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var f in Directory.EnumerateFiles(a.Path, "*", option).Order(StringComparer.Ordinal)) yield return f;
    }

    public static Args? Parse(string[] args)
    {
        if (args.Length < 2 || args[0] is "-h" or "--help") { Usage(); return null; }
        string path = args[0];
        string uri = args[1];
        string protocol = uri.Split(':')[0].ToLowerInvariant();
        ReadOnlyMemory<byte> serverKey = default, clientKey = default;
        double? overhead = null; // adaptive
        double rate = 10, blockTimeout = 2, noProgress = 60;
        int lanes = 0, level = 2, smallSlots = Route.SmallSlots, largeSlots = Route.LargeSlots;
        long maxSize = 1024 * 1024;
        bool recursive = false, confirm = true, usePrefix = true;
        string prefix = $"{DateTime.UtcNow:yyyyMMddHHmmss}";

        for (int i = 2; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--server-key": serverKey = Convert.FromBase64String(Next() ?? ""); break;
                case "--client-key": clientKey = Convert.FromBase64String(Next() ?? ""); break;
                case "--overhead":
                    var value = Next() ?? "x";
                    overhead = value == "auto" ? null : double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--rate-mbps": rate = double.Parse(Next() ?? "x"); break;
                case "--lanes": lanes = int.Parse(Next() ?? "x"); break;
                case "--max-size":
                    if (!TryParseSize(Next(), out maxSize)) { Console.Error.WriteLine("ERROR: --max-size takes bytes, or a number with KiB, MiB, GiB or TiB"); return null; }
                    break;
                case "--small-slots": smallSlots = int.Parse(Next() ?? "x"); break;
                case "--large-slots": largeSlots = int.Parse(Next() ?? "x"); break;
                case "--block-timeout": blockTimeout = double.Parse(Next() ?? "x"); break;
                case "--timeout": noProgress = double.Parse(Next() ?? "x"); break;
                case "--prefix": prefix = Next() ?? prefix; break;
                case "--no-prefix": usePrefix = false; break;
                case "--recursive": recursive = true; break;
                case "--no-confirm": confirm = false; break;
                case "--verbose": level = 3; break;
                case "--silent": level = 0; break;
                default: Console.Error.WriteLine($"Unknown or incomplete option: {args[i]}"); Usage(); return null;
            }
        }
        if (!File.Exists(path) && !Directory.Exists(path)) { Console.Error.WriteLine($"ERROR: path not found: {path}"); return null; }
        if (smallSlots < 1 || largeSlots < 1 || smallSlots + largeSlots > 256)
        {
            Console.Error.WriteLine("ERROR: --small-slots and --large-slots must be at least 1, and 256 or fewer in total");
            return null;
        }
        if (protocol is not ("udp" or "wg")) { Console.Error.WriteLine($"ERROR: unsupported endpoint: {uri}"); return null; }
        if (protocol == "wg" && (serverKey.Length != 32 || clientKey.Length != 32))
        {
            Console.Error.WriteLine("ERROR: wg:// needs --server-key and --client-key (32 bytes, base64)");
            return null;
        }
        var parts = uri.Split([':', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !int.TryParse(parts[2], out var port) || port is < 1 or > 65535)
        {
            Console.Error.WriteLine($"ERROR: invalid endpoint URI: {uri}");
            return null;
        }
        var address = IPAddress.TryParse(parts[1], out var ip) ? ip : Dns.GetHostAddresses(parts[1]).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        return new Args(path, protocol, new IPEndPoint(address, port), serverKey, clientKey, overhead, rate, lanes,
            blockTimeout, noProgress, recursive, confirm, level, usePrefix, prefix, smallSlots, largeSlots, maxSize);
    }

    private static bool TryParseSize(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var units = new (string Suffix, long Scale)[] { ("TiB", 1L << 40), ("GiB", 1L << 30), ("MiB", 1L << 20), ("KiB", 1L << 10), ("B", 1), ("", 1) };
        foreach (var (suffix, scale) in units)
        {
            if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!double.TryParse(text[..^suffix.Length], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0) return false;
            bytes = (long)Math.Min(n * scale, Layout.MaxObjectBytes);
            return true;
        }
        return false;
    }

    private static void Usage() => Console.WriteLine("""

        Usage:
          raptor2 <file|folder> <udp|wg>://<host>:<port> [options]

            Files must be 1 MiB or smaller unless --max-size says otherwise; use aws s3 cp for large files.

            --max-size <n>        Largest file to send (default 1MiB; accepts KiB, MiB, GiB, TiB). Files over 16 MiB
                                  are sent as multipart uploads across the large-file lanes.

            --rate-mbps <n>       Send rate limit in Mbps (default 10). One MicroVM absorbs about 300 Mbps.
            --lanes <n>           Parts sent at once (default: from the rate).
            --small-slots <n>     MicroVMs to spread files across (default 4: 4 x 2 GB fits the default 8 GB quota).
            --large-slots <n>     MicroVMs for the parts of large files (default 60). Each slot in use is one
                                  MicroVM, so slots x MemoryMiB must fit the account's MicroVM memory quota.
            --overhead <f|auto>   Repair symbols sent up front, as a fraction of K, or auto (default): adapt to the
                                  measured loss, down to none on a clean link.
            --block-timeout <s>   Longest wait before repairing missing blocks (default 2).
            --timeout <s>         Give up after this long without progress (default 60).
            --recursive           Include subfolders.
            --prefix <p>          Key prefix (default: a UTC timestamp). --no-prefix uses relative paths only.
            --no-confirm          Fire and forget: do not wait for the backend to confirm.
            --server-key/--client-key <base64>   WireGuard keys (for wg://).
            --verbose | --silent

        Examples:
          raptor2 foo.zip udp://203.0.113.42:2048 --rate-mbps 1000
          raptor2 photos wg://203.0.113.42:2048 --server-key <b64> --client-key <b64> --recursive
        """);
}
