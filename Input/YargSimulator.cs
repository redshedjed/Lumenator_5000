using System.Diagnostics;
using System.Net;

namespace YargDmx.Input;

/// <summary>A stand-in for YARG: sends sACN like YARG does (same channels, 44 frames/s, 60 ms pulses)
/// for a looping demo song, so the interpreter and the visualizer can be tuned without the game.
/// Each segment is one cue for some bars; snares on 2 and 4 (with a keyframe in keyframed cues), kicks
/// on 1 and 3.</summary>
public sealed class YargSimulator : IDisposable
{
    public sealed record Segment(Cue Cue, int Bars, Strobe Strobe = Strobe.Off, bool Fog = false);

    public static readonly Segment[] Demo =
    {
        new(Cue.Intro, 4),
        new(Cue.BlackoutFast, 1),
        new(Cue.Stomp, 4),
        new(Cue.Frenzy, 4),
        new(Cue.Searchlights, 4),
        new(Cue.Verse, 4),
        new(Cue.Chorus, 3),
        new(Cue.Chorus, 1, Strobe.Fast),
    };

    const double PulseS = 0.060;
    readonly E131Sender _sender;
    readonly ChannelMap _map;
    readonly ushort _universe;
    readonly double _bpm;
    readonly Segment[] _song;
    readonly Thread _thread;
    volatile bool _run = true;

    public YargSimulator(ChannelMap map, ushort universe, double bpm = 140, Segment[]? song = null)
    {
        _map = map;
        _universe = universe;
        _bpm = bpm;
        _song = song ?? Demo;
        _sender = new E131Sender("YARG simulator", IPAddress.Loopback);
        _thread = new Thread(Loop) { IsBackground = true, Name = "yarg-sim" };
        _thread.Start();
    }

    /// <summary>The universe YARG would be sending <paramref name="t"/> seconds into the demo.</summary>
    public byte[] Frame(double t)
    {
        double beatS = 60 / _bpm;
        int songBeats = _song.Sum(s => s.Bars * 4);
        double beatPos = t / beatS % songBeats;
        int beat = (int)beatPos;
        double inBeat = (beatPos - beat) * beatS;          // seconds since this beat
        bool pulse = inBeat < PulseS;

        var seg = _song[0];
        int segStart = 0;
        foreach (var s in _song)
        {
            if (beat < segStart + s.Bars * 4) { seg = s; break; }
            segStart += s.Bars * 4;
        }
        int beatInBar = beat % 4;

        var f = new byte[512];
        void Set(int ch, byte v) { if (ch is >= 1 and <= 512) f[ch - 1] = v; }
        foreach (var d in _map.Dimmer) Set(d, 255);
        Set(_map.Cue, (byte)seg.Cue);
        Set(_map.Strobe, seg.Strobe switch { Strobe.Slow => 64, Strobe.Medium => 127, Strobe.Fast => 191, Strobe.Fastest => 255, _ => 0 });
        Set(_map.Fog, seg.Fog ? (byte)255 : (byte)0);
        if (pulse)
        {
            Set(_map.Beatline, beatInBar == 0 ? (byte)1 : (byte)11);
            var drums = beatInBar % 2 == 0 ? Drum.Kick : Drum.Red;
            if (beatInBar == 0 && (beat - segStart) % 16 == 0) drums |= Drum.GreenCymbal;
            Set(_map.Drums, (byte)drums);
            if (drums.HasFlag(Drum.Red) && seg.Cue is Cue.Stomp or Cue.Dischord or Cue.CoolManual or Cue.WarmManual)
                Set(_map.Keyframe, 3);
        }

        // a stand-in for YARG's own Stage Kit patterns: a blue ring that turns once a bar and green
        // positions that swap every beat (not a copy of any real cue)
        if (seg.Cue is not (Cue.BlackoutFast or Cue.BlackoutSlow or Cue.BlackoutSpotlight))
        {
            int spin = (int)(beatPos * 2) % 8;
            Set(_map.Blue[spin], 255);
            Set(_map.Blue[(spin + 4) % 8], 255);
            for (int i = beat % 2; i < 8; i += 2)
                Set(_map.Green[i], 255);
            if (seg.Cue == Cue.Chorus)
                for (int i = 0; i < 8; i += 2) Set(_map.Red[i], 255);
        }
        return f;
    }

    void Loop()
    {
        var clock = Stopwatch.StartNew();
        while (_run)
        {
            _sender.Send(_universe, Frame(clock.Elapsed.TotalSeconds));
            Thread.Sleep(1000 / 44);
        }
    }

    public void Dispose()
    {
        _run = false;
        _thread.Join(500);
        _sender.Dispose();
    }
}
