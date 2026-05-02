using Amazon.Lambda.Core;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Proxylity.RaptorQ;
using Proxylity.RaptorQ.Storage;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using System.Text.Json.Serialization;
using Amazon.RuntimeDependencies;
using AWSSDK.Extensions.CrtIntegration;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

GlobalRuntimeDependencyRegistry.Instance.RegisterChecksumProvider(new CrtChecksums());

var instance = new Function();
var serializer = new SourceGeneratorLambdaJsonSerializer<JsonContext>();
await LambdaBootstrapBuilder.Create<SQSEvent>(instance.FunctionHandler, serializer)
    .Build()
    .RunAsync();

/// <summary>
/// Block completer lambda — invoked asynchronously from the completion queue after the
/// packet ingestor has enqueued a block for decode. Responsibilities:
///   1. Ensure the S3 multipart upload record exists (creates it if this is the first block
///      to complete for this objectId, handling the race with a conditional PutItem).
///   2. Read all stored symbols from DDB, run Gauss-Jordan decode, and upload the block
///      as an S3 multipart part.
///   3. Atomically increment the completed-block counter.
///   4. If all blocks are done, finalize the S3 multipart upload — guarded by a
///      conditional write on IsFinalized so only one Lambda ever calls CompleteMultipartUpload.
///   5. On any failure, release block-scoped state so a later completion message can retry.
/// </summary>
public class Function(IAmazonDynamoDB ddb, IAmazonS3 s3, IAmazonSimpleNotificationService sns)
{
    private readonly IAmazonDynamoDB _dynamoDb = ddb;
    private readonly IAmazonS3 _s3 = s3;
    private readonly IAmazonSimpleNotificationService _sns = sns;

    private static readonly string TABLE_NAME = Environment.GetEnvironmentVariable(nameof(TABLE_NAME))
        ?? throw new InvalidOperationException($"{nameof(TABLE_NAME)} environment variable is not set.");
    private static readonly string BUCKET_NAME = Environment.GetEnvironmentVariable(nameof(BUCKET_NAME))
        ?? throw new InvalidOperationException($"{nameof(BUCKET_NAME)} environment variable is not set.");
    private static readonly string BUCKET_PREFIX = Environment.GetEnvironmentVariable(nameof(BUCKET_PREFIX)) ?? "decoded/";
    private static readonly string REPLY_TOPIC_ARN = Environment.GetEnvironmentVariable(nameof(REPLY_TOPIC_ARN))
        ?? throw new InvalidOperationException($"{nameof(REPLY_TOPIC_ARN)} environment variable is not set.");

    public Function() : this(new AmazonDynamoDBClient(), new AmazonS3Client(), new AmazonSimpleNotificationServiceClient()) { }

    // ── Entry point ────────────────────────────────────────────────────────────

    public async Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        using var cts = new CancellationTokenSource(context.RemainingTime - TimeSpan.FromMilliseconds(200));
        var batchItemFailures = new List<SQSBatchResponse.BatchItemFailure>();

        var records = sqsEvent.Records
            .Select(r => (mid: r.MessageId, payload: System.Text.Json.JsonSerializer.Deserialize(r.Body, JsonContext.Default.BlockCompleterPayload)))
            .Where(p => p.payload != null)
            .ToList();
        var payloadsByObjectId = records.GroupBy(p => p.payload!.ObjectId);

        var tasks = payloadsByObjectId.Select(async group =>
        {
            foreach ((var mid, var payload) in group)
            {
                context.Logger.LogInformation(
                    $"Block completer started: messageId={mid} objectId={payload!.ObjectId} block={payload.BlockIndex}/{payload.NumBlocks - 1}");

                try
                {
                    var overall = await EnsureOverallMetadata(
                        payload.ObjectId, payload.NumBlocks, payload.OriginalSize, cts.Token);

                    for (int i = 0; i < 3; ++i) {
                        try {
                            await DecodeAndUploadBlock(payload, overall, cts.Token);
                            break; // success
                        } catch (InvalidOperationException ex) {
                            context.Logger.LogWarning(ex, $"Attempt {i+1} to decode/upload block {payload.BlockIndex} of '{payload.ObjectId}' failed: {ex.Message}");
                            if (i == 2) throw; // rethrow on last attempt
                            await Task.Delay(200); // wait before retrying
                        }
                    }

                    int completedBlocks = await IncrementCompletedBlocks(payload.ObjectId, cts.Token);

                    context.Logger.LogInformation(
                        $"Block decoded and uploaded: objectId={payload.ObjectId} block={payload.BlockIndex} " +
                        $"completedBlocks={completedBlocks}/{payload.NumBlocks}");

                    if (completedBlocks == payload.NumBlocks)
                    {
                        await FinaliseTransfer(payload.ObjectId, payload.NumBlocks, overall.S3UploadId, cts.Token, context);
                        context.Logger.LogInformation(
                            $"Transfer finalized: objectId={payload.ObjectId} " +
                            $"s3=s3://{BUCKET_NAME}/{BUCKET_PREFIX}{payload.ObjectId}");
                        await NotifyTransferCompletion(payload, context, cts.Token);
                    } else await NotifyBlockCompletion(payload, context, cts.Token);
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(ex, $"Error processing block completion for messageId={mid} objectId={payload.ObjectId} block={payload.BlockIndex}\n{ex}");
                    batchItemFailures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = mid });
                }
            }
        });
        await Task.WhenAll(tasks);
        return new SQSBatchResponse { BatchItemFailures = batchItemFailures };
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

    private async Task NotifyBlockCompletion(BlockCompleterPayload payload, ILambdaContext context, CancellationToken token)
    {
        await NotifyTransferState(
            remote: System.Net.IPEndPoint.Parse(payload.RemoteEp),
            peerKey: payload.PeerKey,
            egressRegion: payload.EgressRegion,
            objectIdBytes: System.Text.Encoding.UTF8.GetBytes(payload.ObjectId),
            blockIndex: (byte)payload.BlockIndex,
            notificationType: TransferStatePacket.NotificationTypeEnum.BlockComplete,
            notificationData: 0,
            context: context,
            token: token
        );
    }

    private async Task NotifyTransferCompletion(BlockCompleterPayload payload, ILambdaContext context, CancellationToken token)
    {
        await NotifyTransferState(
            remote: System.Net.IPEndPoint.Parse(payload.RemoteEp),
            peerKey: payload.PeerKey,
            egressRegion: payload.EgressRegion,
            objectIdBytes: System.Text.Encoding.UTF8.GetBytes(payload.ObjectId),
            blockIndex: (byte)payload.BlockIndex,
            notificationType: TransferStatePacket.NotificationTypeEnum.TransferComplete,
            notificationData: 0,
            context: context,
            token: token
        );
    }

    // ── Overall (transfer-level) metadata ──────────────────────────────────────

    /// <summary>
    /// Creates the overall transfer metadata record and the S3 multipart upload the first
    /// time any block completer runs for this objectId.  Uses a conditional PutItem so
    /// only one Lambda wins; the loser aborts its redundant upload and reads the winner's record.
    /// </summary>
    private async Task<OverallMetadataRecord> EnsureOverallMetadata(
        string objectId, int numBlocks, long totalOriginalSize, CancellationToken token)
    {
        const string overallSK = "OVERALL";
        var overallPK = $"METADATA|{objectId}";

        // Fast path: record already exists (most block completions will hit this path).
        var getResp = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = TABLE_NAME,
            Key = new() {
                { "PK", new() { S = overallPK } },
                { "SK", new() { S = overallSK } }
            }
        }, token);

        if (getResp.Item?.Count > 0)
        {
            return new OverallMetadataRecord
            {
                ObjectId = objectId,
                NumBlocks = numBlocks,
                TotalOriginalSize = totalOriginalSize,
                CompletedBlocks = int.Parse(getResp.Item["CompletedBlocks"].N),
                S3UploadId = getResp.Item["S3UploadId"].S,
                IsFinalized = getResp.Item.TryGetValue("IsFinalized", out var fin) && fin.BOOL == true
            };
        }

        // Slow path: initiate the S3 multipart upload and race to write the overall record.
        var mpResp = await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = BUCKET_NAME,
            Key = $"{BUCKET_PREFIX}{objectId}"
        }, token);

        long ttl = DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeSeconds();
        try
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = TABLE_NAME,
                ConditionExpression = "attribute_not_exists(PK)",
                Item = new()
                    {
                    { "PK",                new() { S = overallPK                    } },
                    { "SK",                new() { S = overallSK                    } },
                    { "NumBlocks",         new() { N = numBlocks.ToString()         } },
                    { "TotalOriginalSize", new() { N = totalOriginalSize.ToString() } },
                    { "CompletedBlocks",   new() { N = "0"                          } },
                    { "S3UploadId",        new() { S = mpResp.UploadId              } },
                    { "Expires",           new() { N = ttl.ToString()               } }
                }
            }, token);

            return new OverallMetadataRecord
            {
                ObjectId = objectId,
                NumBlocks = numBlocks,
                TotalOriginalSize = totalOriginalSize,
                CompletedBlocks = 0,
                S3UploadId = mpResp.UploadId
            };
        }
        catch (ConditionalCheckFailedException)
        {
            // Another block completer wrote first — abort our upload and use theirs.
            await _s3.AbortMultipartUploadAsync(
                BUCKET_NAME, $"{BUCKET_PREFIX}{objectId}", mpResp.UploadId, token);

            var existing = await _dynamoDb.GetItemAsync(new GetItemRequest
            {
                TableName = TABLE_NAME,
                Key = new() {
                    { "PK", new() { S = overallPK } },
                    { "SK", new() { S = overallSK } }
                }
            }, token);

            return new OverallMetadataRecord
            {
                ObjectId = objectId,
                NumBlocks = numBlocks,
                TotalOriginalSize = totalOriginalSize,
                CompletedBlocks = int.Parse(existing.Item["CompletedBlocks"].N),
                S3UploadId = existing.Item["S3UploadId"].S,
                IsFinalized = existing.Item.TryGetValue("IsFinalized", out var fin) && fin.BOOL == true
            };
        }
    }

    // ── Decode + S3 upload ─────────────────────────────────────────────────────

    /// <summary>
    /// Queries all stored symbol items for the block from DDB, runs Gauss-Jordan decode,
    /// uploads the reconstructed block as an S3 multipart part, and records the ETag.
    /// </summary>
    private async Task DecodeAndUploadBlock(
        BlockCompleterPayload payload, OverallMetadataRecord overall, CancellationToken token)
    {
        var packets = await GetStoredPackets(payload.ObjectId, payload.BlockIndex, token);

        if (packets.Count < payload.K)
            throw new InvalidOperationException(
                $"Deferred — not enough symbols stored yet for block {payload.BlockIndex} of " +
                $"'{payload.ObjectId}' (stored={packets.Count}, K={payload.K}). Will retry.");

        // Block-scoped seed key — must match the encoder's per-block seed derivation.
        var decoder = new RaptorQDecoder(payload.K, payload.SymbolSize);

        var syms = packets.Select(p => Convert.FromBase64String(p.Base64Data)).ToArray();
        var idxs = packets.Select(p => p.PacketIndex).ToArray();

        if (!decoder.Decode(syms, idxs))
            throw new InvalidOperationException(
                $"Decode failed for block {payload.BlockIndex} of '{payload.ObjectId}': " +
                $"insufficient linearly-independent symbols (received={packets.Count}, K={payload.K}).");

        var blockData = decoder.ReconstructOriginalObject(payload.BlockDataSize);

        // Each source block is ≤ 512 KiB, below S3's 5 MiB minimum for non-last multipart
        // parts.  Write the decoded bytes to a staging S3 object; FinaliseTransfer will
        // accumulate BlocksPerPart staging objects into each S3 multipart part once all
        // blocks for this transfer are decoded.
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = BUCKET_NAME,
            Key = StagingKey(payload.ObjectId, payload.BlockIndex),
            InputStream = new MemoryStream(blockData)
        }, token);
    }

    /// <summary>Atomically increments CompletedBlocks on the OVERALL item; returns the new count.</summary>
    private async Task<int> IncrementCompletedBlocks(string objectId, CancellationToken token)
    {
        var resp = await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = TABLE_NAME,
            Key = new() {
                { "PK", new() { S = $"METADATA|{objectId}" } },
                { "SK", new() { S = "OVERALL"              } }
            },
            UpdateExpression = "ADD CompletedBlocks :one",
            ExpressionAttributeValues = new() { { ":one", new() { N = "1" } } },
            ReturnValues = ReturnValue.ALL_NEW
        }, token);
        return int.Parse(resp.Attributes["CompletedBlocks"].N);
    }

    /// <summary>
    /// Collects every block's ETag and calls CompleteMultipartUpload to assemble the final
    /// S3 object.  Protected by a conditional write on IsFinalized so exactly one Lambda
    /// completes the upload even if multiple block completers race on the last two blocks.
    /// </summary>
    private async Task FinaliseTransfer(
        string objectId, int numBlocks, string uploadId, CancellationToken token, ILambdaContext context)
    {
        // Win the finalization race with a conditional write — only the winner proceeds.
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = TABLE_NAME,
                Key = new() {
                    { "PK", new() { S = $"METADATA|{objectId}" } },
                    { "SK", new() { S = "OVERALL"              } }
                },
                UpdateExpression = "SET IsFinalized = :t",
                ConditionExpression = "attribute_not_exists(IsFinalized)",
                ExpressionAttributeValues = new()
                {
                    { ":t", new() { BOOL = true } }
                }
            }, token);
        }
        catch (ConditionalCheckFailedException)
        {
            context.Logger.LogInformation(
                $"FinaliseTransfer: another Lambda already completed upload for objectId={objectId}");
            return;
        }

        // Accumulate decoded blocks into S3 multipart parts.  Source blocks are 512 KiB;
        // S3 requires non-last parts >= 5 MiB.  BlocksPerPart = ceil(5*1024*1024 / (512*1024)) = 10
        // ensures every non-last part satisfies the minimum.  The last part may be smaller.
        // If MAX_BLOCK_BYTES changes in the encoder Lambda or CLI, update this constant.
        const int blocksPerPart = 10;
        int numParts = (numBlocks + blocksPerPart - 1) / blocksPerPart;
        var partETags = new List<PartETag>(numParts);

        for (int partIdx = 0; partIdx < numParts; partIdx++)
        {
            int firstBlock = partIdx * blocksPerPart;
            int blockCount = Math.Min(blocksPerPart, numBlocks - firstBlock);

            // Download all staging objects for this part concurrently.
            var downloads = await Task.WhenAll(
                Enumerable.Range(firstBlock, blockCount)
                    .Select(b => DownloadStagingBlockAsync(objectId, b, token)));

            // Concatenate into one contiguous buffer for the part upload.
            int totalBytes = downloads.Sum(d => d.Length);
            var partData = new byte[totalBytes];
            int offset = 0;
            foreach (var chunk in downloads) { chunk.CopyTo(partData, offset); offset += chunk.Length; }

            var uploadResp = await _s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = BUCKET_NAME,
                Key = $"{BUCKET_PREFIX}{objectId}",
                UploadId = uploadId,
                PartNumber = partIdx + 1,
                InputStream = new MemoryStream(partData),
                PartSize = partData.Length
            }, token);
            partETags.Add(new PartETag(partIdx + 1, uploadResp.ETag));

            // Delete staging objects now that the part is safely uploaded.
            await Task.WhenAll(
                Enumerable.Range(firstBlock, blockCount)
                    .Select(b => _s3.DeleteObjectAsync(new DeleteObjectRequest
                    {
                        BucketName = BUCKET_NAME,
                        Key = StagingKey(objectId, b)
                    }, token)));
        }

        await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = BUCKET_NAME,
            Key = $"{BUCKET_PREFIX}{objectId}",
            UploadId = uploadId,
            PartETags = partETags
        }, token);

        context.Logger.LogInformation($"Object complete: objectId={objectId} numBlocks={numBlocks}");
    }

    // ── Packet retrieval ───────────────────────────────────────────────────────

    /// <summary>
    /// Retrieves all stored symbols for a single source block.
    /// Packets are spread across 6 sub-partitions ('a'–'f').  All 6 queries are fired
    /// concurrently to eliminate serial round-trip latency, and a ProjectionExpression
    /// fetches only the two attributes needed for decoding (skip ObjectId, BlockIndex,
    /// Timestamp, Expires) to reduce payload size and DDB read throughput consumption.
    /// </summary>
    private async Task<List<PacketRecord>> GetStoredPackets(
        string objectId, int blockIndex, CancellationToken token)
    {
        var pkBase = $"PACKET|{objectId}|{blockIndex:D6}";

        var tasks = PacketRecord.PartitionSuffixes
            .Select(suffix => QueryPartitionAsync(objectId, blockIndex, $"{pkBase}|{suffix}", token))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        return [.. results.SelectMany(r => r)];
    }

    private async Task<List<PacketRecord>> QueryPartitionAsync(
        string objectId, int blockIndex, string pk, CancellationToken token)
    {
        var packets = new List<PacketRecord>();

        await foreach (var page in _dynamoDb.Paginators
          .Query(new QueryRequest
          {
              TableName = TABLE_NAME,
              KeyConditionExpression = "PK = :pk",
              ExpressionAttributeValues = new() { { ":pk", new() { S = pk } } },
              // Only fetch the two attributes the decoder needs.
              ProjectionExpression = "PacketIndex, #d",
              ExpressionAttributeNames = new() { { "#d", "Data" } }
          })
          .Responses.WithCancellation(token))
        {
            foreach (var item in page.Items)
            {
                packets.Add(new PacketRecord
                {
                    ObjectId = objectId,
                    BlockIndex = blockIndex,
                    PacketIndex = int.Parse(item["PacketIndex"].N),
                    Base64Data = item["Data"].S
                });
            }
        }

        return packets;
    }

    // ── S3 staging helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// S3 key for the staging object holding one decoded source block.
    /// Staging objects live under <c>staging/</c> and are deleted by
    /// <see cref="FinaliseTransfer"/> after each S3 multipart part is uploaded.
    /// </summary>
    private static string StagingKey(string objectId, int blockIndex) =>
        $"staging/{objectId}/block-{blockIndex:D6}";

    /// <summary>Downloads a staging block object and returns its bytes.</summary>
    private async Task<byte[]> DownloadStagingBlockAsync(
        string objectId, int blockIndex, CancellationToken token)
    {
        using var resp = await _s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = BUCKET_NAME,
            Key = StagingKey(objectId, blockIndex)
        }, token);
        using var ms = new MemoryStream((int)resp.ContentLength);
        await resp.ResponseStream.CopyToAsync(ms, token);
        return ms.ToArray();
    }
}

public record ProxylityEndpoint(string Address, int Port, string PeerKey);
public record ProxylityReplyMessage(ProxylityEndpoint Remote, string Data);
public record ProxylityResponse(List<ProxylityReplyMessage> Messages);


[JsonSerializable(typeof(ProxylityEndpoint))]
[JsonSerializable(typeof(ProxylityReplyMessage))]
[JsonSerializable(typeof(ProxylityResponse))]
[JsonSerializable(typeof(BlockCompleterPayload))]
[JsonSerializable(typeof(List<BlockCompleterPayload>))]
[JsonSerializable(typeof(SQSEvent))]
public partial class JsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
