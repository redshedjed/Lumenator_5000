using System.Runtime.InteropServices;
using YargDmx.Input;

namespace YargDmx.Output;

public static class Universe
{
    static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    /// <summary>The 512 DMX slots for these fixture colours. With a dimmer channel the colour goes out at
    /// full and the dimmer carries the level; with a white channel, white takes the common part. The fog
    /// channel, if there is one, is 255 while <paramref name="fog"/> is on.</summary>
    public static byte[] Build(Rig rig, IReadOnlyList<Rgb> colours, bool fog = false)
    {
        var u = new byte[512];
        if (rig.Output.FogChannel is >= 1 and <= 512)
            u[rig.Output.FogChannel - 1] = fog ? (byte)255 : (byte)0;
        for (int i = 0; i < rig.Fixtures.Count; i++)
        {
            var f = rig.Fixtures[i];
            var p = rig.Profiles[f.Profile];
            var c = colours[i];
            void Set(int offset, byte v) => u[f.Address + offset - 2] = v;
            foreach (var (offset, v) in p.Fixed)
                Set(int.Parse(offset), v);
            double level = 1;
            if (p.Dimmer is int d)
            {
                level = Math.Max(c.R, Math.Max(c.G, c.B));
                Set(d, B(level));
                c = level > 0 ? c.Scale(1 / level) : Rgb.Black;
            }
            double white = 0;
            if (p.White is int w)
            {
                white = Math.Min(c.R, Math.Min(c.G, c.B));
                Set(w, B(white));
                c = new Rgb(c.R - white, c.G - white, c.B - white);
            }
            Set(p.Red, B(c.R));
            Set(p.Green, B(c.G));
            Set(p.Blue, B(c.B));
            foreach (var (offset, colour) in p.Copy)
                Set(int.Parse(offset), B(colour switch { "red" => c.R, "green" => c.G, "blue" => c.B, _ => white }));
        }
        return u;
    }

    /// <summary>What each fixture would show for these slots (the visualizer's "fake DMX" view).</summary>
    public static Rgb[] Read(Rig rig, byte[] u)
    {
        var o = new Rgb[rig.Fixtures.Count];
        for (int i = 0; i < o.Length; i++)
        {
            var f = rig.Fixtures[i];
            var p = rig.Profiles[f.Profile];
            double Get(int offset) => u[f.Address + offset - 2] / 255.0;
            double white = p.White is int w ? Get(w) : 0;
            var c = new Rgb(Get(p.Red) + white, Get(p.Green) + white, Get(p.Blue) + white);
            o[i] = p.Dimmer is int d ? c.Scale(Get(d)) : c;
        }
        return o;
    }
}

public interface IDmxOutput : IDisposable
{
    string Name { get; }
    void Send(byte[] universe);
}

public sealed class SacnOutput(ushort universe) : IDmxOutput
{
    readonly E131Sender _sender = new("YARG DMX interpreter");
    public string Name => $"sACN universe {universe}";
    public void Send(byte[] u) => _sender.Send(universe, u);
    public void Dispose() => _sender.Dispose();
}

/// <summary>An FTDI USB-DMX cable ("Open DMX" style: the PC does the DMX timing) through FTDI's D2XX
/// driver (ftd2xx.dll, installed with the FTDI driver). Its own thread sends the latest universe
/// continuously, as DMX wants. UNTESTED: written to the FTDI and Open DMX docs without hardware.</summary>
public sealed class FtdiOutput : IDmxOutput
{
    [DllImport("ftd2xx.dll")] static extern uint FT_Open(int index, out IntPtr handle);
    [DllImport("ftd2xx.dll")] static extern uint FT_Close(IntPtr handle);
    [DllImport("ftd2xx.dll")] static extern uint FT_ResetDevice(IntPtr handle);
    [DllImport("ftd2xx.dll")] static extern uint FT_SetDivisor(IntPtr handle, ushort divisor);
    [DllImport("ftd2xx.dll")] static extern uint FT_SetDataCharacteristics(IntPtr handle, byte bits, byte stop, byte parity);
    [DllImport("ftd2xx.dll")] static extern uint FT_SetFlowControl(IntPtr handle, ushort flow, byte xon, byte xoff);
    [DllImport("ftd2xx.dll")] static extern uint FT_ClrRts(IntPtr handle);
    [DllImport("ftd2xx.dll")] static extern uint FT_Purge(IntPtr handle, uint mask);
    [DllImport("ftd2xx.dll")] static extern uint FT_SetBreakOn(IntPtr handle);
    [DllImport("ftd2xx.dll")] static extern uint FT_SetBreakOff(IntPtr handle);
    [DllImport("ftd2xx.dll")] static extern uint FT_Write(IntPtr handle, byte[] buffer, uint count, out uint written);

    const uint Ok = 0;
    IntPtr _h;
    readonly byte[] _frame = new byte[513];       // start code 0 + 512 slots
    readonly object _lock = new();
    readonly Thread _thread;
    volatile bool _run = true;

    public string Name => "FTDI USB-DMX";

    public FtdiOutput(int index = 0)
    {
        if (FT_Open(index, out _h) != Ok)
            throw new IOException($"no FTDI device {index}");
        FT_ResetDevice(_h);
        FT_SetDivisor(_h, 12);                    // 3 MHz / 12 = 250 kbaud
        FT_SetDataCharacteristics(_h, 8, 2, 0);   // 8 bits, 2 stop bits, no parity
        FT_SetFlowControl(_h, 0, 0, 0);
        FT_ClrRts(_h);
        FT_Purge(_h, 3);
        _thread = new Thread(Loop) { IsBackground = true, Name = "dmx-ftdi" };
        _thread.Start();
    }

    public void Send(byte[] u)
    {
        lock (_lock) Buffer.BlockCopy(u, 0, _frame, 1, 512);
    }

    void Loop()
    {
        var buf = new byte[513];
        while (_run)
        {
            lock (_lock) Buffer.BlockCopy(_frame, 0, buf, 0, 513);
            FT_SetBreakOn(_h);
            Thread.Sleep(1);                      // break >= 88 us
            FT_SetBreakOff(_h);
            if (FT_Write(_h, buf, (uint)buf.Length, out _) != Ok)
            {
                Console.WriteLine("FTDI: write failed; stopping DMX output");
                return;
            }
            Thread.Sleep(20);                     // ~40 frames/s with the 22 ms frame time
        }
    }

    public void Dispose()
    {
        _run = false;
        _thread.Join(500);
        Array.Clear(_frame);
        if (_h != IntPtr.Zero)
        {
            FT_Write(_h, _frame, (uint)_frame.Length, out _);   // lights off
            FT_Close(_h);
        }
    }
}
