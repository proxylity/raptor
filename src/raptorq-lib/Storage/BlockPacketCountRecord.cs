namespace Proxylity.RaptorQ.Storage;

/// <summary>
/// DDB item that tracks the number of packets received for a single source block.
///
/// This item is updated by the packet ingestor as symbols arrive and provides the
/// trigger used to generate completion-queue messages for the block completer.
///
/// Key: PK = "COUNT|{ObjectId}"  SK = "BLOCK|{BlockIndex:D6}"
/// </summary>
public class BlockPacketCountRecord
{
  public string PK { get => $"COUNT|{ObjectId}";   set { } }
  public string SK { get => $"BLOCK|{BlockIndex:D6}"; set { } }

  public string ObjectId        { get; set; } = string.Empty;

  public int    BlockIndex      { get; set; }

  public int    ReceivedPackets { get; set; }

  /// <summary>Unix epoch seconds at which DynamoDB should expire this item.</summary>
  public long   Expires         { get; set; } = DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeSeconds();
}
