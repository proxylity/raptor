namespace Proxylity.RaptorQ.Storage;

/// <summary>
/// Payload passed from the packet ingestor to the block completer lambda via the
/// completion FIFO queue. All fields needed for decode are carried directly so the
/// block completer can start work without an extra metadata lookup first.
/// </summary>
public record BlockCompleterPayload(
    string ObjectId,
    int    BlockIndex,
    int    K,
    int    SymbolSize,
    int    NumBlocks,
    long   OriginalSize,
    long   BlockDataSize,
    string EgressRegion,
    string RemoteEp,
    string PeerKey);
