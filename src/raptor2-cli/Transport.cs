using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Raptor2.Cli;

public interface ITransport : IDisposable
{
    /// <summary>Sends one datagram synchronously. Called from the wire thread, and for the occasional STATUS.</summary>
    void Send(ReadOnlySpan<byte> datagram);

    /// <summary>Sends buffer[offsets[i]..+lengths[i]] for i in [0, count), in as few system calls as the platform allows.</summary>
    void SendMany(byte[] buffer, ReadOnlySpan<int> offsets, ReadOnlySpan<int> lengths, int count)
    {
        for (int i = 0; i < count; i++) Send(buffer.AsSpan(offsets[i], lengths[i]));
    }
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
}

public sealed class UdpTransport : ITransport
{
    private readonly Socket _socket;

    public UdpTransport(IPEndPoint endpoint)
    {
        _socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
        {
            SendBufferSize = 4 * 1024 * 1024,
            ReceiveBufferSize = 4 * 1024 * 1024,
        };
        _socket.Connect(endpoint);
    }

    // A connected UDP socket reports ICMP errors (port unreachable, and so on) on the next call. UDP delivery is
    // best effort anyway: the protocol repairs losses, so these are ignored rather than ending the transfer.
    private static bool Transient(SocketException e) => e.SocketErrorCode is SocketError.ConnectionRefused
        or SocketError.ConnectionReset or SocketError.HostUnreachable or SocketError.NetworkUnreachable
        or SocketError.NoBufferSpaceAvailable or SocketError.MessageSize;

    public void Send(ReadOnlySpan<byte> datagram)
    {
        try { _socket.Send(datagram, SocketFlags.None); }
        catch (SocketException e) when (Transient(e)) { }
    }

    public void SendMany(byte[] buffer, ReadOnlySpan<int> offsets, ReadOnlySpan<int> lengths, int count)
    {
        if (LinuxBatchSend.TrySend(_socket, buffer, offsets, lengths, count)) return;
        for (int i = 0; i < count; i++) Send(buffer.AsSpan(offsets[i], lengths[i]));
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        while (true)
        {
            try { return await _socket.ReceiveAsync(buffer, SocketFlags.None, ct); }
            catch (SocketException e) when (Transient(e)) { }
        }
    }

    public void Dispose() => _socket.Dispose();
}

public sealed class WireGuardTransport(IPEndPoint endpoint, ReadOnlyMemory<byte> serverKey, ReadOnlyMemory<byte> clientKey) : ITransport
{
    private readonly Proxylity.WireGuardClient.WireGuardClient _wg = new(endpoint, serverKey, clientKey);

    public void Send(ReadOnlySpan<byte> datagram) => _wg.SendAsync(datagram.ToArray(), CancellationToken.None).GetAwaiter().GetResult();

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var result = await _wg.ReceiveAsync(ct);
        var n = Math.Min(result.Buffer.Length, buffer.Length);
        result.Buffer.AsMemory(0, n).CopyTo(buffer);
        return n;
    }

    public void Dispose() { }
}

/// <summary>
/// A run of datagrams (usually one block's) laid end to end in one pooled buffer. Handing whole blocks to the wire
/// thread keeps the producer/consumer handoff at one per block rather than one per packet.
/// </summary>
public sealed class DatagramBatch
{
    private int _length;
    private readonly List<int> _ends = new(600);

    public DatagramBatch(int capacityBytes, Func<bool>? cancelled = null)
    {
        Buffer = ArrayPool<byte>.Shared.Rent(capacityBytes);
        Cancelled = cancelled;
    }

    public byte[] Buffer { get; }
    public Func<bool>? Cancelled { get; }
    /// <summary>Completed once every datagram has been handed to the socket (or the batch was dropped).</summary>
    public TaskCompletionSource? Sent { get; set; }
    public int Count => _ends.Count;
    public Span<byte> Next => Buffer.AsSpan(_length);
    public void Commit(int length) { _length += length; _ends.Add(_length); }
    public int End(int i) => _ends[i];
}

/// <summary>
/// Owns the socket's send side on one dedicated thread: synchronous sends, paced by a token bucket that sleeps in about
/// 1 ms steps. Async sends with Task.Delay pacing cost about 27 us of CPU per packet at 30 Mbps; this costs a syscall.
/// </summary>
public sealed class WireSender : IDisposable
{
    private readonly ITransport _transport;
    private readonly double _bytesPerSecond;
    private readonly Channel<DatagramBatch> _queue;
    private readonly Thread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _next;
    private long _packets, _bytes;

    public WireSender(ITransport transport, double mbps, int queuedBatches = 8)
    {
        _transport = transport;
        _bytesPerSecond = mbps * 1_000_000 / 8;
        _queue = Channel.CreateBounded<DatagramBatch>(new BoundedChannelOptions(queuedBatches) { SingleReader = true });
        _thread = new Thread(Run) { IsBackground = true, Name = "raptor2-wire", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public long Packets => Interlocked.Read(ref _packets);
    public long Bytes => Interlocked.Read(ref _bytes);

    public ValueTask EnqueueAsync(DatagramBatch batch, CancellationToken ct) => _queue.Writer.WriteAsync(batch, ct);

    /// <summary>Sends one datagram outside the queue (STATUS polls): unpaced, from the caller's thread.</summary>
    public void SendNow(ReadOnlySpan<byte> datagram) => _transport.Send(datagram);

    private void Run()
    {
        var reader = _queue.Reader;
        var offsets = new int[LinuxBatchSend.MaxBatch];
        var lengths = new int[LinuxBatchSend.MaxBatch];
        // Sleep only once a quantum ahead: about 16 packets' worth, 2-8 ms. Sleeping every millisecond costs far more
        // in wake-ups than it gains in smoothness.
        double quantum = _bytesPerSecond <= 0 ? 0 : Math.Clamp(16 * 1100 / _bytesPerSecond, 0.002, 0.008);
        while (true)
        {
            if (!reader.TryRead(out var batch))
            {
                if (!reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()) return;
                continue;
            }
            try
            {
                int i = 0, start = 0;
                while (i < batch.Count)
                {
                    if (batch.Cancelled?.Invoke() == true) break; // the object completed while this batch waited
                    // Gather what the pacer allows now (up to one system call's worth), send it, then sleep if ahead.
                    int n = 0;
                    double ahead = 0;
                    while (i < batch.Count && n < LinuxBatchSend.MaxBatch)
                    {
                        int end = batch.End(i), length = end - start;
                        ahead = Reserve(length);
                        offsets[n] = start; lengths[n] = length; n++;
                        _bytes += length;
                        start = end; i++;
                        if (ahead > quantum) break;
                    }
                    _transport.SendMany(batch.Buffer, offsets, lengths, n);
                    _packets += n;
                    if (ahead > quantum) Thread.Sleep(TimeSpan.FromSeconds(ahead));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(batch.Buffer);
                batch.Sent?.TrySetResult();
            }
        }
    }

    /// <summary>Accounts for <paramref name="bytes"/> in the token bucket; returns how far ahead of the rate the sender now is, in seconds.</summary>
    private double Reserve(int bytes)
    {
        if (_bytesPerSecond <= 0) return 0;
        double now = _clock.Elapsed.TotalSeconds;
        if (_next < now - 0.010) _next = now - 0.010; // at most 10 ms of burst credit after an idle spell
        _next += bytes / _bytesPerSecond;
        return _next - now;
    }

    public void Complete() => _queue.Writer.TryComplete();

    public void Dispose()
    {
        Complete();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
