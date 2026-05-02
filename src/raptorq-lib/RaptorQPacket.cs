using System.Buffers.Binary;
using System.Text;

namespace Proxylity.RaptorQ;

// Wire format (all integers big-endian per RFC 6330 §§8.1-8.2, except object_id which is UTF-8):
//
//   [1 byte]  id_length
//   [N bytes] object_id  (UTF-8)
//
//   Common FEC OTI (RFC 6330 §8.1, 8 bytes):
//     [5 bytes] transfer_length F  (unsigned 40-bit big-endian)
//     [1 byte]  reserved = 0
//     [2 bytes] symbol_size T      (unsigned 16-bit big-endian)
//
//   Scheme-specific FEC OTI (RFC 6330 §8.2, 4 bytes):
//     [1 byte]  Z = num_source_blocks (must be 1 for this implementation)
//     [2 bytes] N = sub-blocks (always 1)
//     [1 byte]  Al = symbol alignment (always 4)
//
//   FEC Payload ID (RFC 6330 §8.3, 4 bytes):
//     [1 byte]  SBN = source block number (0-based, 0..Z-1)
//     [3 bytes] ESI = encoding symbol ID  (0-based within block)
//
//   [*]       symbol_data
//
// Minimum header with 36-byte UUID: 1+36 + 8 + 4 + 4 = 53 bytes.

/// <summary>
/// Represents a single RaptorQ packet as serialised to/from a UDP datagram (RFC 6330 §8).
/// </summary>
public class RaptorQPacket
{
    public const int FIXED_SYMBOL_HEADER_LENGTH = 8 + 4 + 4 + 1; // Common OTI + Scheme OTI + FEC Payload ID + Object ID Length

    public string ObjectId { get; set; } = string.Empty;

    // --- Common OTI (RFC §8.1) ---
    /// <summary>Total transfer (object) length in bytes, F.</summary>
    public long OriginalSize { get; set; }
    /// <summary>Symbol size T in bytes.</summary>
    public int SymbolSize { get; set; }

    // --- Scheme OTI (RFC §8.2) ---
    /// <summary>Number of source blocks Z (must be 1..255).</summary>
    public int NumBlocks { get; set; }

    // --- FEC Payload ID (RFC §8.3) ---
    /// <summary>Source block number (0-based).</summary>
    public int BlockIndex { get; set; }
    /// <summary>Encoding symbol ID within the block (0-based ESI).</summary>
    public int PacketIndex { get; set; }

    public Memory<byte> Data { get; set; }

    public static RaptorQPacket FromBytes(Memory<byte> bytes)
    {
        var span  = bytes.Span;
        if (span.Length < FIXED_SYMBOL_HEADER_LENGTH + 1)
            throw new ArgumentException("Buffer too short to contain a valid RaptorQ packet header.", nameof(bytes));
        int idLen = span[0];
        if (span.Length < 1 + idLen + FIXED_SYMBOL_HEADER_LENGTH)
            throw new ArgumentException("Buffer too short for declared object ID length.", nameof(bytes));
        var id    = bytes.Slice(1, idLen);
        int off   = 1 + idLen;

        // Common OTI
        long F    = ((long)span[off] << 32) | ((long)span[off+1] << 24) |
                    ((long)span[off+2] << 16) | ((long)span[off+3] << 8) | span[off+4];
        // span[off+5] = reserved
        int T     = BinaryPrimitives.ReadUInt16BigEndian(span[(off+6)..]);
        off += 8;

        // Scheme OTI
        int Z  = span[off];
        // span[off+1..off+2] = N (ignored; always 1)
        // span[off+3] = Al (ignored; always 4)
        off += 4;

        // FEC Payload ID
        int SBN = span[off];
        int ESI = (span[off+1] << 16) | (span[off+2] << 8) | span[off+3];
        off += 4;

        return new RaptorQPacket
        {
            ObjectId      = Encoding.UTF8.GetString(id.Span),
            OriginalSize  = F,
            SymbolSize    = T,
            NumBlocks     = Z,
            BlockIndex    = SBN,
            PacketIndex   = ESI,
            Data          = bytes[off..]
        };
    }

    public int WriteTo(Span<byte> destination)
    {
        if (NumBlocks < 1 || NumBlocks > 255)
            throw new ArgumentOutOfRangeException(nameof(destination), "NumBlocks must be 1..255.");
        if ((uint)PacketIndex > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(destination), "PacketIndex (ESI) must be 0..16777215.");

        int idLen = Encoding.UTF8.GetBytes(ObjectId.AsSpan(), destination[1..]);
        destination[0] = (byte)idLen;
        int off = 1 + idLen;

        // Common OTI: 5-byte F + 1-byte reserved + 2-byte T
        destination[off]   = (byte)(OriginalSize >> 32);
        destination[off+1] = (byte)(OriginalSize >> 24);
        destination[off+2] = (byte)(OriginalSize >> 16);
        destination[off+3] = (byte)(OriginalSize >> 8);
        destination[off+4] = (byte) OriginalSize;
        destination[off+5] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(destination[(off+6)..], (ushort)SymbolSize);
        off += 8;

        // Scheme OTI: Z + N(1) + Al(4)
        destination[off]   = (byte)NumBlocks;
        destination[off+1] = 0;
        destination[off+2] = 1;
        destination[off+3] = 4;
        off += 4;

        // FEC Payload ID: SBN + ESI(3 bytes)
        destination[off]   = (byte)BlockIndex;
        destination[off+1] = (byte)(PacketIndex >> 16);
        destination[off+2] = (byte)(PacketIndex >> 8);
        destination[off+3] = (byte) PacketIndex;
        off += 4;

        Data.Span.CopyTo(destination[off..]);
        return off + Data.Length;
    }

    public byte[] ToBytes()
    {
        int idLen  = Encoding.UTF8.GetByteCount(ObjectId);
        var result = new byte[1 + idLen + 8 + 4 + 4 + Data.Length];
        WriteTo(result);
        return result;
    }
}