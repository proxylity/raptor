using System.Text.Json;
using Raptor2.MicroVm;

// Ports: Proxylity delivers packet batches to 8080. Lambda calls its lifecycle hooks on 9000, kept
// separate so the token Proxylity holds (scoped to 8080) cannot reach the hooks.
const int PacketPort = 8080;
const int HookPort = 9000;
const string RunHookPath = "/aws/lambda-microvms/runtime/v1/run";
const string ReadyHookPath = "/aws/lambda-microvms/runtime/v1/ready";

// For local runs: RAPTOR_SINK_DIR writes objects to a directory instead of S3; RAPTOR_PORT moves the packet port.
var localDir = Environment.GetEnvironmentVariable("RAPTOR_SINK_DIR");
var packetPort = int.TryParse(Environment.GetEnvironmentVariable("RAPTOR_PORT"), out var envPort) ? envPort : PacketPort;
var local = localDir is not null;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
    options.ListenAnyIP(packetPort);
    if (!local) options.ListenAnyIP(HookPort);
});

var app = builder.Build();

// Per-MicroVM values come from the /run hook, not from startup: Lambda snapshots the app after it starts.
var microvmId = "unknown";
string? tenantId = null;
var settings = new AppSettings(null, null, null, true);
IObjectSink? sink = localDir is null ? null : new FileObjectSink(localDir);
var ready = false;
app.Lifetime.ApplicationStarted.Register(() => ready = true);

static void Emit(string json) => Console.Out.WriteLine(json);
long Budget() => (settings.MaxBufferMiB is { } mib ? mib * 1024L * 1024
    : (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * 0.6));

var engine = new Engine(() => sink, () => settings.Prefix ?? "", Budget, e =>
{
    if (settings.LogCommits ?? true || e.Error is not null)
        Emit(JsonSerializer.Serialize(e with { Microvm = microvmId }, AppJson.Default.CommitEvent));
});
var processor = new BatchProcessor(engine);

static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> OnlyOnPort(int port) =>
    (context, next) => context.HttpContext.Connection.LocalPort == port
        ? next(context)
        : ValueTask.FromResult<object?>(Results.NotFound());

// ---- Packet port ----

app.MapPost("/{**path}", async Task<IResult> (HttpContext context) =>
{
    GatewayRequest? request;
    try { request = await context.Request.ReadFromJsonAsync(AppJson.Default.GatewayRequest, context.RequestAborted); }
    catch (JsonException) { request = null; }
    if (request is null) return Results.BadRequest();
    return Results.Json(await processor.ProcessAsync(request, context.RequestAborted), AppJson.Default.GatewayResponse);
}).AddEndpointFilter(OnlyOnPort(packetPort));

app.MapGet("/{**path}", () => Results.Json(
    new StatusDocument("ok", microvmId, tenantId, engine.ObjectCount, processor.Batches, processor.Packets,
        engine.BufferBytes, engine.Commits, engine.DecodeAttempts, engine.Dropped),
    AppJson.Default.StatusDocument)).AddEndpointFilter(OnlyOnPort(packetPort));

// ---- Hook port ----

if (!local)
{
    app.MapPost(ReadyHookPath, () => ready ? Results.Text("{}", "application/json") : Results.StatusCode(503))
        .AddEndpointFilter(OnlyOnPort(HookPort));

    app.MapPost(RunHookPath, async (HttpContext context) =>
    {
        try
        {
            var run = await context.Request.ReadFromJsonAsync(AppJson.Default.RunRequest, context.RequestAborted);
            microvmId = run?.MicrovmId ?? microvmId;
            RunHookPayload? payload = string.IsNullOrEmpty(run?.RunHookPayload)
                ? null
                : JsonSerializer.Deserialize(run.RunHookPayload, AppJson.Default.RunHookPayload);
            tenantId = payload?.TenantId;
            if (!string.IsNullOrWhiteSpace(payload?.ExtraData))
                settings = JsonSerializer.Deserialize(payload.ExtraData, AppJson.Default.AppSettings) ?? settings;
            if (string.IsNullOrEmpty(settings.Bucket))
            {
                Console.Error.WriteLine("Run hook failed: RunHookExtraData must set Bucket");
                return Results.StatusCode(500);
            }
            sink = new S3ObjectSink(settings.Bucket, payload?.Region);
            Emit(JsonSerializer.Serialize(new RunEvent("run", microvmId, tenantId, payload?.Region, settings), AppJson.Default.RunEvent));
            return Results.Text("{}", "application/json");
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"Run hook failed: {exception.Message}");
            return Results.StatusCode(500);
        }
    }).AddEndpointFilter(OnlyOnPort(HookPort));
}

// ---- Housekeeping ----

_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
    var wasBusy = false;
    while (await timer.WaitForNextTickAsync())
    {
        var removed = engine.Sweep(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20));
        var busy = engine.ObjectCount > 0 || removed > 0;
        if (busy || wasBusy)
            Emit(JsonSerializer.Serialize(new StatsEvent("stats", microvmId, engine.ObjectCount, processor.Batches, processor.Packets,
                engine.BufferBytes, engine.Commits, engine.Dropped), AppJson.Default.StatsEvent));
        wasBusy = busy;
    }
});

app.Run();
