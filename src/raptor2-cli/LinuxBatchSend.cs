using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Raptor2.Cli;

/// <summary>
/// sendmmsg(2) for a connected UDP socket on Linux: up to <see cref="MaxBatch"/> datagrams per system call. On other
/// platforms (or if the call is unavailable) <see cref="TrySend"/> returns false and the caller sends one at a time.
/// </summary>
internal static unsafe partial class LinuxBatchSend
{
    public const int MaxBatch = 32;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec { public byte* Base; public nuint Length; }

    // glibc x86_64/aarch64 layouts: struct msghdr is 56 bytes, struct mmsghdr 64.
    [StructLayout(LayoutKind.Sequential)]
    private struct MsgHdr
    {
        public void* Name; public uint NameLength;
        public IoVec* Iov; public nuint IovLength;
        public void* Control; public nuint ControlLength;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MMsgHdr { public MsgHdr Header; public uint Length; }

    [LibraryImport("libc", EntryPoint = "sendmmsg", SetLastError = true)]
    private static partial int SendMMsg(int fd, MMsgHdr* messages, uint count, int flags);

    private const int ECONNREFUSED = 111, EAGAIN = 11, ENOBUFS = 105, EINTR = 4;

    private static readonly bool Available = OperatingSystem.IsLinux() && sizeof(MMsgHdr) == 64;
    private static bool _failed;

    /// <summary>
    /// Sends datagrams buffer[offsets[i]..offsets[i]+lengths[i]] for i in [0, count). Returns false if batching is not
    /// available, in which case nothing was sent. Transient errors (ICMP port unreachable, full buffers) drop the
    /// datagram concerned, as a one-at-a-time send would.
    /// </summary>
    public static bool TrySend(Socket socket, byte[] buffer, ReadOnlySpan<int> offsets, ReadOnlySpan<int> lengths, int count)
    {
        if (!Available || _failed) return false;
        if (count == 0) return true;
        int fd = (int)socket.SafeHandle.DangerousGetHandle();
        var iov = stackalloc IoVec[MaxBatch];
        var messages = stackalloc MMsgHdr[MaxBatch];
        fixed (byte* basePtr = buffer)
        {
            int sent = 0;
            while (sent < count)
            {
                int n = Math.Min(MaxBatch, count - sent);
                for (int i = 0; i < n; i++)
                {
                    iov[i] = new IoVec { Base = basePtr + offsets[sent + i], Length = (nuint)lengths[sent + i] };
                    messages[i] = default;
                    messages[i].Header.Iov = &iov[i];
                    messages[i].Header.IovLength = 1;
                }
                int r;
                try { r = SendMMsg(fd, messages, (uint)n, 0); }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
                {
                    _failed = true;
                    return sent == 0 ? false : throw new InvalidOperationException("sendmmsg became unavailable mid-batch", e);
                }
                if (r > 0) { sent += r; continue; }
                int errno = Marshal.GetLastPInvokeError();
                if (errno == EINTR) continue;
                if (errno is ECONNREFUSED or EAGAIN or ENOBUFS) { sent++; continue; } // drop one, like a lossy link
                throw new SocketException(errno);
            }
        }
        return true;
    }
}
