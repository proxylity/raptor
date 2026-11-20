// A stand-in for the Proxylity UDP Gateway, for local development and tests only.
//
// Listens on a UDP port and batches packets per tenant (byte 1 of each datagram, the same value the real destination's
// TenantIdExpression [1:2] extracts). A batch is sent when it reaches <count> packets or <ms> after its first packet.
// Each tenant maps to one of the app URLs (tenant % urls), so several local MicroVM processes can stand in for slots.
//
//   dotnet run --project src/raptor2-localgw -- [udpPort=5001] [appUrls=http://localhost:8080/] [count=1000] [ms=100] [loss=0]
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

var udpPort = args.Length > 0 ? int.Parse(args[0]) : 5001;
var urls = (args.Length > 1 ? args[1] : "http://localhost:8080/").Split(',');
var count = args.Length > 2 ? int.Parse(args[2]) : 1000;
var ms = args.Length > 3 ? int.Parse(args[3]) : 100;
var loss = args.Length > 4 ? double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 0; // simulated inbound loss
var rng = new Random(1);

using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, udpPort));
udp.Client.ReceiveBufferSize = 16 * 1024 * 1024;
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var tags = new ConcurrentDictionary<string, IPEndPoint>();
var pending = new Dictionary<byte, (List<(IPEndPoint From, byte[] Data, DateTime At)> Items, DateTime Deadline)>();
var gate = new object();
long tagCounter = 0, batches = 0, packets = 0;

Console.WriteLine($"raptor2-localgw: udp/{udpPort} -> {string.Join(", ", urls)} (batch {count} / {ms} ms)");

_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(Math.Max(1, ms / 10));
        List<(byte Tenant, List<(IPEndPoint, byte[], DateTime)> Items)> due = [];
        lock (gate)
            foreach (var (tenant, (items, deadline)) in pending.ToArray())
                if (DateTime.UtcNow >= deadline) { due.Add((tenant, items)); pending.Remove(tenant); }
        foreach (var (tenant, items) in due) _ = Task.Run(() => Deliver(tenant, items));
    }
});

while (true)
{
    var r = await udp.ReceiveAsync();
    if (r.Buffer.Length < 2) continue;
    if (loss > 0 && rng.NextDouble() < loss) continue;
    byte tenant = r.Buffer[1];
    List<(IPEndPoint, byte[], DateTime)>? flush = null;
    lock (gate)
    {
        if (!pending.TryGetValue(tenant, out var entry)) entry = ([], DateTime.UtcNow.AddMilliseconds(ms));
        entry.Items.Add((r.RemoteEndPoint, r.Buffer, DateTime.UtcNow));
        if (entry.Items.Count >= count) { flush = entry.Items; pending.Remove(tenant); }
        else pending[tenant] = entry;
    }
    if (flush is not null) { var items = flush; _ = Task.Run(() => Deliver(tenant, items)); }
}

async Task Deliver(byte tenant, List<(IPEndPoint From, byte[] Data, DateTime At)> items)
{
    var messages = items.Select(p =>
    {
        var tag = Interlocked.Increment(ref tagCounter).ToString();
        tags[tag] = p.From;
        return new
        {
            Tag = tag,
            Remote = new { IpAddress = p.From.Address.ToString(), Port = p.From.Port },
            Local = new { Domain = "local", Port = udpPort },
            Formatter = "base64",
            Data = Convert.ToBase64String(p.Data),
            ReceivedAt = p.At.ToString("O"),
        };
    }).ToArray();
    Interlocked.Increment(ref batches);
    Interlocked.Add(ref packets, messages.Length);
    try
    {
        var url = urls[tenant % urls.Length];
        var response = await http.PostAsync(url, new StringContent(JsonSerializer.Serialize(new { Messages = messages }), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var reply in document.RootElement.GetProperty("Replies").EnumerateArray())
        {
            if (!tags.TryGetValue(reply.GetProperty("Tag").GetString()!, out var to)) continue;
            var data = Convert.FromBase64String(reply.GetProperty("Data").GetString()!);
            await udp.SendAsync(data, data.Length, to);
        }
    }
    catch (Exception e) { Console.Error.WriteLine($"Delivery failed: {e.Message}"); }
    foreach (var m in messages) tags.TryRemove(m.Tag, out _);
}
