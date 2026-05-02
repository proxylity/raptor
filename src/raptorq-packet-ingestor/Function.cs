using Amazon.Lambda.Core;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Proxylity.RaptorQ;
using Proxylity.RaptorQ.Storage;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Amazon.RuntimeDependencies;
using AWSSDK.Extensions.CrtIntegration;
using Amazon.Lambda.SQSEvents;
using Amazon.SQS;
using Amazon.SimpleNotificationService.Model;
using Amazon.SimpleNotificationService;

GlobalRuntimeDependencyRegistry.Instance.RegisterChecksumProvider(new CrtChecksums());

var instance = new Function();
var serializer = new SourceGeneratorLambdaJsonSerializer<JsonContext>();
await LambdaBootstrapBuilder.Create<SQSEvent>(instance.FunctionHandler, serializer)
    .Build()
    .RunAsync();

/// <summary>
/// Packet ingestor lambda — receives batched UDP packets from the Proxylity Gateway,
/// parses them, atomically updates the per-block received-packet counter, and stores
/// raw symbol data in DynamoDB. Decode/upload decisions are handled downstream by the
/// completion queue and block completer lambda.
/// </summary>
public class Function(IAmazonDynamoDB ddb, IAmazonSQS sqsClient, IAmazonSimpleNotificationService snsClient)
{
    private readonly IAmazonDynamoDB _dynamoDb = ddb;
    private readonly IAmazonSQS _sqsClient = sqsClient;
    private readonly IAmazonSimpleNotificationService _sns = snsClient;

    private static readonly string TABLE_NAME = Environment.GetEnvironmentVariable(nameof(TABLE_NAME))
        ?? throw new InvalidOperationException($"{nameof(TABLE_NAME)} environment variable is not set.");
    private static readonly string BLOCK_COMPLETER_QUEUE_URL =
        Environment.GetEnvironmentVariable(nameof(BLOCK_COMPLETER_QUEUE_URL))
        ?? throw new InvalidOperationException($"{nameof(BLOCK_COMPLETER_QUEUE_URL)} environment variable is not set.");
    private static readonly string REPLY_TOPIC_ARN = Environment.GetEnvironmentVariable(nameof(REPLY_TOPIC_ARN))
        ?? throw new InvalidOperationException($"{nameof(REPLY_TOPIC_ARN)} environment variable is not set.");

    public Function() : this(new AmazonDynamoDBClient(), new AmazonSQSClient(), new AmazonSimpleNotificationServiceClient()) { }

    public async Task FunctionHandler(Amazon.Lambda.SQSEvents.SQSEvent sqsEvent, ILambdaContext context)
    {
        // Reserve 200 ms for cleanup; the remaining processing budget drives StorePackets calls.
        using var cts = new CancellationTokenSource(context.RemainingTime - TimeSpan.FromMilliseconds(200));
        context.Logger.LogInformation($"Received {sqsEvent.Records.Count} SQS record(s).");
        context.Logger.LogInformation($"Bodies: {string.Join("\n", sqsEvent.Records.Select(r => r.Body))}");

        try
        {
            var messages = sqsEvent.Records.Select(r => JsonNode.Parse(r.Body)?.AsObject() 
                ?? throw new Exception("Failed to parse SQS message body as JSON object."))
            .ToList();

            await ProcessRequest(messages, context, cts.Token);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error processing SQS record: {ex}");
        }
    }

    private async Task ProcessRequest(IEnumerable<JsonObject> messages, ILambdaContext context, CancellationToken token)
    {
        try
        {
            var packets = messages
                .Select(j =>
                {
                    var base64 = j?["Data"]?.GetValue<string>()
                        ?? throw new Exception("Message missing 'Data' field.");
                    return (message: j, raptor: RaptorQPacket.FromBytes(Convert.FromBase64String(base64)));
                })
                .ToList();

            if (packets.Count > 0)
            {
                var groups = packets.GroupBy(p => (p.raptor.ObjectId, p.raptor.BlockIndex)).ToList();
                var tasks = groups.Select(group => ProcessPacketGroup(group, context, token));
                await Task.WhenAll(tasks);

                context.Logger.LogInformation($"Ingested {packets.Count} packets across {groups.Count} block(s).");                
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            context.Logger.LogError("Lambda invocation cancelled (near timeout).");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"FunctionHandler unhandled error: {ex}");
        }
    }

    private async Task ProcessPacketGroup(IGrouping<(string ObjectId, int BlockIndex), (JsonObject message, RaptorQPacket raptor)> group, ILambdaContext context, CancellationToken token)
    {
        var objectId = group.Key.ObjectId;
        var blockIndex = group.Key.BlockIndex;
        var groupPackets = group.Select(g => g.raptor).ToList();
        var first = group.First();
        var egress_region = first.message["IngressRegion"]?.GetValue<string>() ?? string.Empty;

        // store packets to ensure they are available for the block completer
        var written = await StorePackets(groupPackets, context, token);

        // update the block metadata to signal the arrival of the new packets
        var remote_ip = System.Net.IPAddress.TryParse(first.message["Remote"]?["IpAddress"]?.GetValue<string>(), out var ip) ? ip 
            : throw new Exception("Failed to parse remote IP address from message.");
        var remote_port = first.message["Remote"]?["Port"]?.GetValue<int>() ?? 
            throw new Exception("Failed to parse remote port from message.");
        var peer_key = first.message["Remote"]?["PeerKey"]?.GetValue<string>() ?? string.Empty;

        var remote_ep = new System.Net.IPEndPoint(remote_ip, remote_port);
        var blockCount = await UpdateBlockPacketCount(objectId, blockIndex, written, token);

        // MAX_BLOCK_BYTES must match the encoder (both Lambda and CLI use 512 * 1024).
        const long maxBlockBytes = 512 * 1024;
        long blockStart    = (long)first.raptor.BlockIndex * maxBlockBytes;
        long blockDataSize = Math.Min(blockStart + maxBlockBytes, first.raptor.OriginalSize) - blockStart;
        int  K             = (int)Math.Ceiling((double)blockDataSize / first.raptor.SymbolSize);

        if (blockCount.ReceivedPackets >= K) {
            var payload = new BlockCompleterPayload(
                ObjectId: first.raptor.ObjectId,
                BlockIndex: first.raptor.BlockIndex,
                K: K,
                SymbolSize: first.raptor.SymbolSize,
                NumBlocks: first.raptor.NumBlocks,
                OriginalSize: first.raptor.OriginalSize,
                BlockDataSize: blockDataSize,
                EgressRegion: egress_region,
                RemoteEp: remote_ep.ToString(),
                PeerKey: peer_key
            );
            await Task.WhenAll(
                QueueBlockForCompletion(payload, blockCount, context, token),
                NotifyPacketsSufficientForBlock(payload, context, token)
            );
        } else await NotifyTransferState(
            remote_ep, 
            peer_key, 
            egress_region, 
            System.Text.Encoding.UTF8.GetBytes(objectId), 
            (byte)blockIndex, 
            0x00, // packet arrival notification -- a batch ACK
            (ushort)blockCount.ReceivedPackets, 
            context, 
            token);

        context.Logger.LogInformation(
            $"After update: object={objectId} block={blockIndex} recv={blockCount.ReceivedPackets}/{K}");
    }

    private async Task NotifyTransferState(System.Net.IPEndPoint remote, string peerKey, string egressRegion, ReadOnlyMemory<byte> objectIdBytes, byte blockIndex, TransferStatePacket.NotificationTypeEnum notificationType, ushort notificationData, ILambdaContext context, CancellationToken token)
    {
        var notification = new TransferStatePacket
        {
            ObjectId = System.Text.Encoding.UTF8.GetString(objectIdBytes.Span),
            BlockIndex = blockIndex,
            NotificationType = notificationType,
            NotificationData = notificationData
        };
        var data = notification.ToBytes();
        var reply = new ProxylityResponse(Messages: [
            new ProxylityReplyMessage(
                Remote: new ProxylityEndpoint(Address: remote.Address.ToString(), Port: remote.Port, PeerKey: peerKey),
                Data: Convert.ToBase64String(data)
            )
        ]);
        await _sns.PublishAsync(new PublishRequest
        {
            TopicArn = REPLY_TOPIC_ARN,
            MessageAttributes = new()
            {
                ["EgressRegion"] = new MessageAttributeValue { DataType = "String", StringValue = egressRegion }
            },
            Message = System.Text.Json.JsonSerializer.Serialize(reply, JsonContext.Default.ProxylityResponse)
        }, token);
    }

    private async Task NotifyPacketsSufficientForBlock(BlockCompleterPayload payload, ILambdaContext context, CancellationToken token)
    {
        await NotifyTransferState(
            remote: System.Net.IPEndPoint.Parse(payload.RemoteEp),
            peerKey: payload.PeerKey,
            egressRegion: payload.EgressRegion,
            objectIdBytes: System.Text.Encoding.UTF8.GetBytes(payload.ObjectId),
            blockIndex: (byte)payload.BlockIndex,
            notificationType: TransferStatePacket.NotificationTypeEnum.BlockPacketsSufficient,
            notificationData: 0,
            context: context,
            token: token
        );
    }

    private async Task QueueBlockForCompletion(BlockCompleterPayload payload, BlockPacketCountRecord blockCount, ILambdaContext context, CancellationToken token)
    {
        var request = new Amazon.SQS.Model.SendMessageRequest
        {
            QueueUrl = BLOCK_COMPLETER_QUEUE_URL,
            MessageGroupId = $"{payload.ObjectId}_{payload.BlockIndex}",
            MessageDeduplicationId = $"{payload.BlockIndex}",
            MessageBody = System.Text.Json.JsonSerializer.Serialize(payload, JsonContext.Default.BlockCompleterPayload)
        };

        try
        {
            await _sqsClient.SendMessageAsync(request, token);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error sending message batch to SQS: {ex.Message}");
        }
        context.Logger.LogInformation($"completer dispatched: objectId={payload.ObjectId} blockIndex={payload.BlockIndex} K={payload.K} recv={blockCount.ReceivedPackets} egressRegion={payload.EgressRegion}");
    }

    /// <summary>
    /// Atomically increments ReceivedPackets and (on first write) initialises the block packet count item.
    /// </summary>
    /// <param name="objectId">The ID of the object.</param>
    /// <param name="blockIndex">The index of the block.</param>
    /// <param name="count">The number of packets to increment.</param>
    /// <param name="token">The cancellation token.</param>
    /// <returns>The updated block packet count record.</returns>
    private async Task<BlockPacketCountRecord> UpdateBlockPacketCount(string objectId, int blockIndex, int count, CancellationToken token)
    {
        long ttl = DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeSeconds();
        var resp = await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = TABLE_NAME,
            Key = BlockPacketCountKey(objectId, blockIndex),
            UpdateExpression = "ADD ReceivedPackets :inc SET Expires = :ttl",
            ExpressionAttributeValues = new()
            {
                { ":inc", new() { N = count.ToString() } },
                { ":ttl", new() { N = ttl.ToString() } }
            },
            ReturnValues = ReturnValue.ALL_NEW
        }, token);

        var a = resp.Attributes;
        return new BlockPacketCountRecord
        {
            ObjectId = objectId,
            BlockIndex = blockIndex,
            ReceivedPackets = int.Parse(a["ReceivedPackets"].N)
        };
    }

    private async Task<int> StorePackets(List<RaptorQPacket> packets, ILambdaContext context, CancellationToken token)
    {
        long ttl = DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeSeconds();
        var nows = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var writes = packets.Select(p =>
        {
            var pr = new PacketRecord(p);
            return new WriteRequest
            {
                PutRequest = new PutRequest
                {
                    Item = new()
                    {
                        { "PK",          new() { S = pr.PK                                                } },
                        { "SK",          new() { S = pr.SK                                                } },
                        { "ObjectId",    new() { S = p.ObjectId                                           } },
                        { "BlockIndex",  new() { N = pr.BlockIndex.ToString()                             } },
                        { "PacketIndex", new() { N = pr.PacketIndex.ToString()                            } },
                        { "Data",        new() { S = pr.Base64Data                                        } },
                        { "Timestamp",   new() { N = nows } },
                        { "Expires",     new() { N = ttl.ToString()                                       } }
                    }
                }
            };
        }).ToList();

        int tries = 0;
        while (writes.Count > 0 && tries < 5)
        {
            tries++;
            var chunks = writes.Chunk(25);
            var tasks = chunks.Select(batch => _dynamoDb.BatchWriteItemAsync(new BatchWriteItemRequest()
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>
                {
                    { TABLE_NAME, batch.ToList() }
                }
            }, token)
            .ContinueWith(t => t.Result.UnprocessedItems.TryGetValue(TABLE_NAME, out var unprocessed) ? unprocessed : [], token));

            var results = await Task.WhenAll([.. tasks]);
            var remaining = results.SelectMany(r => r).ToList();

            if (remaining.Count > 0) { // log how many are unprocessed/dropped
                context.Logger.LogWarning($"Warning: {remaining.Count} of {packets.Count} packets were not stored in DynamoDB and are lost.");
                await Task.Delay(10, token);
            }

            writes = [.. remaining];
        }

        return packets.Count - writes.Count; // writes contains any packets that were not successfully stored after retries
    }

    private static Dictionary<string, AttributeValue> BlockPacketCountKey(string objectId, int blockIndex) => new()
    {
        { "PK", new() { S = $"COUNT|{objectId}"   } },
        { "SK", new() { S = $"BLOCK|{blockIndex:D6}" } }
    };
}

public record ProxylityEndpoint(string Address, int Port, string PeerKey);
public record ProxylityReplyMessage(ProxylityEndpoint Remote, string Data);
public record ProxylityResponse(List<ProxylityReplyMessage> Messages);

[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(SQSEvent))]
[JsonSerializable(typeof(BlockCompleterPayload))]
[JsonSerializable(typeof(ProxylityEndpoint))]
[JsonSerializable(typeof(ProxylityReplyMessage))]
[JsonSerializable(typeof(ProxylityResponse))]
public partial class JsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
