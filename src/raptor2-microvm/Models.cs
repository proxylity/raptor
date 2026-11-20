using System.Text.Json.Serialization;

namespace Raptor2.MicroVm;

// The Proxylity batch contract (JSON over HTTPS on port 8080).
public sealed record Endpoint(string IpAddress, int Port);
public sealed record LocalEndpoint(string Domain, int Port);
public sealed record RequestPacket(string Tag, Endpoint? Remote, LocalEndpoint? Local, string? Formatter, string? Data, string? ReceivedAt);
public sealed record GatewayRequest(RequestPacket[]? Messages);
public sealed record ResponsePacket(string Tag, string Data);
public sealed record GatewayResponse(ResponsePacket[] Replies);

// Lambda lifecycle hooks (port 9000).
public sealed record RunRequest(string? MicrovmId, string? RunHookPayload);
public sealed record RunHookPayload(int Version, string? DestinationName, string? ImageArn, string? TenantId, string? Region,
    string? Formatter, int Port, string? StartedAt, string? ExtraData);

/// <summary>Settings from the destination's RunHookExtraData argument (a JSON string).</summary>
public sealed record AppSettings(string? Bucket, string? Prefix, int? MaxBufferMiB, bool? LogCommits);

public sealed record StatusDocument(string Status, string Microvm, string? Tenant, int Objects, long Batches, long Packets,
    long BufferBytes, long Commits, long DecodeAttempts, long Dropped);

public sealed record RunEvent(string Event, string Microvm, string? Tenant, string? Region, AppSettings Settings);
public sealed record CommitEvent(string Event, string Microvm, string Object, string Kind, int Part, long Bytes, long Ms, string? Error);
public sealed record StatsEvent(string Event, string Microvm, int Objects, long Batches, long Packets, long BufferBytes, long Commits, long Dropped);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GatewayRequest))]
[JsonSerializable(typeof(GatewayResponse))]
[JsonSerializable(typeof(RunRequest))]
[JsonSerializable(typeof(RunHookPayload))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(StatusDocument))]
[JsonSerializable(typeof(RunEvent))]
[JsonSerializable(typeof(CommitEvent))]
[JsonSerializable(typeof(StatsEvent))]
public partial class AppJson : JsonSerializerContext;
