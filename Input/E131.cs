using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace YargDmx.Input;

/// <summary>sACN (ANSI E1.31) data packets: just enough to read YARG's output and to fake it.</summary>
public static class E131
{
    public const int Port = 5568;
    const int DataOffset = 126;          // DMX slot 1; slot 0 (start code) is at 125

    static readonly byte[] AcnId = Encoding.ASCII.GetBytes("ASC-E1.17\0\0\0");

    public static IPAddress MulticastGroup(ushort universe) =>
        new(new byte[] { 239, 255, (byte)(universe >> 8), (byte)universe });

    /// <summary>The universe and DMX slots 1..n of a data packet; false for anything else (sync,
    /// discovery, non-zero start codes, truncated packets).</summary>
    public static bool TryParse(ReadOnlySpan<byte> p, out ushort universe, out ReadOnlySpan<byte> slots)
    {
        universe = 0;
        slots = default;
        if (p.Length < DataOffset || !p.Slice(4, 12).SequenceEqual(AcnId))
            return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(p.Slice(18)) != 0x00000004)   // root: E1.31 data
            return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(p.Slice(40)) != 0x00000002)   // framing: DMP
            return false;
        if (p[117] != 0x02 || p[125] != 0x00)                                   // DMP set property, DMX start code
            return false;
        universe = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(113));
        int count = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(123)) - 1;
        count = Math.Clamp(count, 0, Math.Min(512, p.Length - DataOffset));
        slots = p.Slice(DataOffset, count);
        return true;
    }

    /// <summary>A data packet carrying <paramref name="slots"/> (up to 512) on <paramref name="universe"/>.</summary>
    public static byte[] Build(ushort universe, ReadOnlySpan<byte> slots, byte sequence, Guid cid, string source,
                               byte priority = 100)
    {
        int n = Math.Min(512, slots.Length);
        var p = new byte[DataOffset + n];
        var s = p.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(s, 0x0010);
        AcnId.CopyTo(s.Slice(4));
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(16), (ushort)(0x7000 | (p.Length - 16)));
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(18), 0x00000004);
        cid.TryWriteBytes(s.Slice(22, 16));
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(38), (ushort)(0x7000 | (p.Length - 38)));
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(40), 0x00000002);
        var name = Encoding.UTF8.GetBytes(source);
        name.AsSpan(0, Math.Min(63, name.Length)).CopyTo(s.Slice(44, 64));
        p[108] = priority;
        p[111] = sequence;
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(113), universe);
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(115), (ushort)(0x7000 | (p.Length - 115)));
        p[117] = 0x02;
        p[118] = 0xa1;
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(121), 1);
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(123), (ushort)(n + 1));
        slots.Slice(0, n).CopyTo(s.Slice(DataOffset));
        return p;
    }
}

/// <summary>Receives one universe (multicast as YARG sends it, or unicast) and hands each frame on.</summary>
public sealed class E131Receiver : IDisposable
{
    readonly UdpClient _udp;
    readonly ushort _universe;
    readonly CancellationTokenSource _stop = new();
    public long Packets { get; private set; }

    public E131Receiver(ushort universe, Action<byte[]> onFrame)
    {
        _universe = universe;
        _udp = new UdpClient { ExclusiveAddressUse = false };
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, E131.Port));
        try { _udp.JoinMulticastGroup(E131.MulticastGroup(universe)); }
        catch (SocketException e) { Console.WriteLine($"sACN: couldn't join multicast ({e.Message}); unicast only"); }
        _ = Task.Run(() => Loop(onFrame));
    }

    async Task Loop(Action<byte[]> onFrame)
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }   // e.g. ICMP port unreachable echoes on Windows
            if (Frame(r.Buffer) is { } frame)
            {
                Packets++;
                onFrame(frame);
            }
        }
    }

    /// <summary>The 512 slots of a data packet for our universe, else null.</summary>
    byte[]? Frame(byte[] packet)
    {
        if (!E131.TryParse(packet, out var u, out var slots) || u != _universe)
            return null;
        var frame = new byte[512];
        slots.CopyTo(frame);
        return frame;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
    }
}

/// <summary>Sends universes as sACN: multicast by default, or unicast to <paramref name="target"/>.</summary>
public sealed class E131Sender : IDisposable
{
    readonly UdpClient _udp = new();
    readonly Guid _cid = Guid.NewGuid();
    readonly string _name;
    readonly IPAddress? _target;
    byte _seq;

    public E131Sender(string name, IPAddress? target = null)
    {
        _name = name;
        _target = target;
        _udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
    }

    public void Send(ushort universe, ReadOnlySpan<byte> slots)
    {
        var p = E131.Build(universe, slots, _seq++, _cid, _name);
        _udp.Send(p, p.Length, new IPEndPoint(_target ?? E131.MulticastGroup(universe), E131.Port));
    }

    public void Dispose() => _udp.Dispose();
}
