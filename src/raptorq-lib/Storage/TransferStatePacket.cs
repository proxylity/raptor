using System.Buffers.Binary;
using System.Text;

namespace Proxylity.RaptorQ;

// Wire format (object_id is UTF-8):
//   [4 bytes] magic = 'rapt' in ASCII
//   [1 byte]  notification_type
//   [2 bytes] notification_data (meaning depends on type; e.g. received packet count for block ACKs)
//   [1 byte]  block_index
//   [1 byte]  id_length
//   [N bytes] object_id  (UTF-8)
//

/// <summary>
/// Represents a single RaptorQ packet as serialised to/from a UDP datagram (RFC 6330 §8).
/// </summary>
public class TransferStatePacket
{
    public const int MAGIC_BYTES = 0x72617074; // 'r','a','p','t' in hex
    public const int FIXED_SYMBOL_HEADER_LENGTH = 4 + 1 + 2 + 1 + 1; // Magic + Notification Type + Notification Data + Block Index + Object ID Length

    public enum NotificationTypeEnum : byte
    {
        PacketArrival = 0x00,
        BlockPacketsSufficient = 0x01,
        BlockComplete = 0x02,
        TransferComplete = 0x03

    }

    public NotificationTypeEnum NotificationType { get; set; }
    public ushort NotificationData { get; set; }
    public string ObjectId { get; set; } = string.Empty;
    public int BlockIndex { get; set; }

    public static bool TryFromBytes(ReadOnlyMemory<byte> bytes, out TransferStatePacket? packet)
    {
        var span  = bytes.Span;
        var magic = BinaryPrimitives.ReadUInt32BigEndian(span);
        if (magic != MAGIC_BYTES)
        {
            packet = null;
            return false;
        }

        var notificationType = (NotificationTypeEnum)span[4];
        var notificationData = BinaryPrimitives.ReadUInt16BigEndian(span[5..]);
        var blockIndex = span[7];
        var idLen = span[8];
        var id    = bytes.Slice(9, idLen);

        packet = new TransferStatePacket
        {
            NotificationType = notificationType,
            NotificationData = notificationData,
            BlockIndex = blockIndex,
            ObjectId      = Encoding.UTF8.GetString(id.Span),
        };
        return true;
    }

    public int WriteTo(Span<byte> destination)
    {
        int idLen = Encoding.UTF8.GetByteCount(ObjectId);
        if (destination.Length < FIXED_SYMBOL_HEADER_LENGTH + idLen)
            throw new ArgumentException($"Destination buffer too small; need at least {FIXED_SYMBOL_HEADER_LENGTH + idLen} bytes", nameof(destination));

        BinaryPrimitives.WriteUInt32BigEndian(destination, MAGIC_BYTES);
        destination[4] = (byte)NotificationType;
        BinaryPrimitives.WriteUInt16BigEndian(destination[5..], NotificationData);
        destination[7] = (byte)BlockIndex;
        destination[8] = (byte)idLen;
        Encoding.UTF8.GetBytes(ObjectId, destination.Slice(9));

        return FIXED_SYMBOL_HEADER_LENGTH + idLen;
    }

    public byte[] ToBytes()
    {
        int idLen  = Encoding.UTF8.GetByteCount(ObjectId);
        var result = new byte[FIXED_SYMBOL_HEADER_LENGTH + idLen];
        WriteTo(result);
        return result;
    }
}