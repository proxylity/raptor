using Proxylity.RaptorQ;

namespace Proxylity.RaptorQ.Storage;

public class PacketRecord()
{
  public PacketRecord(RaptorQPacket p) : this()
  {
    ObjectId    = p.ObjectId;
    BlockIndex  = p.BlockIndex;
    PacketIndex = p.PacketIndex;
    Base64Data  = Convert.ToBase64String(p.Data.Span);
  }

  /// <summary>DDB hash key: block items are further fanned out across 6 sub-partitions ('a'–'f')
  /// determined by PacketIndex % 6, preventing hot-partition throttling when all packets
  /// for a single block arrive concurrently (e.g. files within one 75 MB source block).</summary>
  public string PK { get => $"PACKET|{ObjectId}|{BlockIndex:D6}|{(char)('a' + PacketIndex % 6)}"; set { } }

  /// <summary>The set of all sub-partition suffixes used by a block.</summary>
  public static readonly char[] PartitionSuffixes = ['a', 'b', 'c', 'd', 'e', 'f'];

  /// <summary>DDB range key: zero-padded PacketIndex for lexicographic ordering within a block.</summary>
  public string SK { get => $"{PacketIndex:D10}"; set { } }

  public string ObjectId    { get; set; } = string.Empty;
  public int    BlockIndex  { get; set; }
  public int    PacketIndex { get; set; }
  public string Base64Data  { get; set; } = string.Empty;
}
