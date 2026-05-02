namespace Proxylity.RaptorQ.Storage;

/// <summary>
/// DDB item tracking overall transfer state, created once per unique objectId.
/// Holds the S3 multipart upload ID so all per-block Lambdas can upload their
/// parts independently and the last one can finalize the upload.
/// Key: PK = "METADATA|{ObjectId}"  SK = "OVERALL"
/// </summary>
public class OverallMetadataRecord
{
  public string PK { get => $"METADATA|{ObjectId}"; set { } }
  public string SK { get => "OVERALL";              set { } }

  public string ObjectId          { get; set; } = string.Empty;

  /// <summary>Total number of source blocks in this transfer.</summary>
  public int    NumBlocks         { get; set; }

  /// <summary>Total transfer size in bytes (across all blocks).</summary>
  public long   TotalOriginalSize { get; set; }

  /// <summary>
  /// Atomic counter incremented each time a block is decoded and its S3 part
  /// uploaded. When this equals NumBlocks, CompleteMultipartUpload is called.
  /// </summary>
  public int    CompletedBlocks   { get; set; }

  /// <summary>
  /// The S3 multipart upload ID for the final assembled object. Created by the
  /// first Lambda to process any packet for this objectId; all subsequent
  /// Lambdas re-use this ID.
  /// </summary>
  public string S3UploadId        { get; set; } = string.Empty;

  /// <summary>
  /// Set to true by the single Lambda invocation that wins the race to call
  /// CompleteMultipartUpload, preventing duplicate completion attempts.
  /// </summary>
  public bool   IsFinalized       { get; set; }

  /// <summary>Unix epoch seconds at which DynamoDB should expire this item.</summary>
  public long   Expires           { get; set; } = DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeSeconds();
}
