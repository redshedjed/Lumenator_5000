using YargDmx.Input;

namespace YargDmx.Engine;

/// <summary>Renders the rig's colours for one moment: the look the current cue maps to, then YARG's
/// strobe on top. Looks only read the YARG state and the clock, so a frame can be rendered at any time;
/// the only memory is chaos's current draw.</summary>
public sealed class Show
{
    readonly Random _random = new();
    long _chaosDraw = -1;
    Rgb[] _chaos = Array.Empty<Rgb>();

    public Rig Rig { get; set; }
    public string LookName { get; private set; } = "";

    public Show(Rig rig) => Rig = rig;

    public Rgb[] Render(YargState s, double now)
    {
        var rig = Rig;
        int n = rig.Fixtures.Count;
        var look = rig.LookFor(s.Cue);
        LookName = rig.Looks.First(kv => ReferenceEquals(kv.Value, look)).Key;
        var colours = look.Colors.Select(rig.Colour).DefaultIfEmpty(Rgb.White).ToArray();

        var o = look.Type switch
        {
            "blackout" => new Rgb[n],
            "solid" => Place(look, colours, n, 0),
            "pulse" => Pulse(look, colours, s, now, n),
            "flash" => Flash(look, colours, s, now, n),
            "chaos" => Chaos(look, colours, s, now, n),
            "police" => Police(look, colours, s, now, n),
            _ => StageKit(rig, s, n),
        };

        if (s.Strobe != Strobe.Off && rig.StrobeHz.TryGetValue(s.Strobe.ToString(), out var hz) && hz > 0)
        {
            bool on = (now * hz) % 1.0 < 0.35;
            for (int i = 0; i < n; i++)
                o[i] = on ? Rgb.White : Rgb.Black;
        }
        for (int i = 0; i < n; i++)
            o[i] = o[i].Scale(Math.Clamp(rig.Master, 0, 1));
        return o;
    }

    /// <summary>Colour i of the look for each fixture, by pattern; <paramref name="shift"/> rotates it.</summary>
    static Rgb[] Place(Look look, Rgb[] colours, int n, int shift)
    {
        var o = new Rgb[n];
        int k = colours.Length;
        for (int i = 0; i < n; i++)
        {
            int c = look.Pattern switch
            {
                "halves" => i < (n + 1) / 2 ? 0 : 1,
                "all" => 0,
                "chase" => i,
                _ => i,          // alternate
            };
            o[i] = colours[((c + shift) % k + k) % k];
        }
        return o;
    }

    static double SinceTrigger(Look look, YargState s, double now) =>
        look.Trigger == "rate"
            ? (now * Math.Max(0.01, look.RateHz)) % 1.0 / Math.Max(0.01, look.RateHz)
            : s.Since(Enum.Parse<Trigger>(look.Trigger, true), now);

    static long TriggerCount(Look look, YargState s, double now) =>
        look.Trigger == "rate"
            ? (long)Math.Floor(now * Math.Max(0.01, look.RateHz))
            : s.Count(Enum.Parse<Trigger>(look.Trigger, true));

    static double Envelope(Look look, double since) =>
        look.Floor + (1 - look.Floor) * Math.Exp(-Math.Max(0, since) / Math.Max(0.01, look.Decay));

    /// <summary>Colours by pattern, brightness kicked on every trigger and fading to the floor. A chase
    /// steps one fixture per trigger.</summary>
    static Rgb[] Pulse(Look look, Rgb[] colours, YargState s, double now, int n)
    {
        // on the beat, the bar line peaks at full and the other beats at 1/accent
        bool downbeat = s.LastAt[(int)Trigger.Downbeat] == s.LastAt[(int)Trigger.Beat];
        double peak = look.Trigger == "beat" && !downbeat ? 1 / Math.Max(1, look.Accent) : 1;
        double level = look.Floor + (peak - look.Floor) * (Envelope(look, SinceTrigger(look, s, now)) - look.Floor)
                       / Math.Max(1e-9, 1 - look.Floor);
        int shift = look.Pattern == "chase" ? (int)(TriggerCount(look, s, now) % Math.Max(1, n)) : 0;
        var o = Place(look, colours, n, shift);
        for (int i = 0; i < n; i++)
            o[i] = o[i].Scale(level);
        return o;
    }

    /// <summary>A flash of the look's first colour (white if none) on every trigger, the second colour (or
    /// black) underneath at the floor level.</summary>
    static Rgb[] Flash(Look look, Rgb[] colours, YargState s, double now, int n)
    {
        double since = SinceTrigger(look, s, now);
        double k = Math.Exp(-Math.Max(0, since) / Math.Max(0.01, look.Decay));
        var top = colours[0];
        var under = (colours.Length > 1 ? colours[1] : Rgb.Black).Scale(look.Floor);
        var o = new Rgb[n];
        for (int i = 0; i < n; i++)
            o[i] = new Rgb(under.R + (top.R - under.R) * k, under.G + (top.G - under.G) * k, under.B + (top.B - under.B) * k);
        return o;
    }

    /// <summary>A fresh random draw on every trigger: <see cref="Look.Density"/> of the fixtures lit, each
    /// in a random colour of the look (random hues if it has none).</summary>
    Rgb[] Chaos(Look look, Rgb[] colours, YargState s, double now, int n)
    {
        long draw = TriggerCount(look, s, now);
        if (draw != _chaosDraw || _chaos.Length != n)
        {
            _chaosDraw = draw;
            _chaos = new Rgb[n];
            for (int i = 0; i < n; i++)
                if (_random.NextDouble() < look.Density)
                    _chaos[i] = look.Colors.Count > 0 ? colours[_random.Next(colours.Length)] : Hue(_random.NextDouble());
        }
        double level = Envelope(look, SinceTrigger(look, s, now));
        return _chaos.Select(c => c.Scale(level)).ToArray();
    }

    /// <summary>Two sides (by pattern: halves or alternate) taking turns, each flashing
    /// <see cref="Look.Flashes"/> times in its colour per turn. Trigger "rate" = flashes per second; any
    /// other trigger paces a whole turn per beat period.</summary>
    static Rgb[] Police(Look look, Rgb[] colours, YargState s, double now, int n)
    {
        int flashes = Math.Max(1, look.Flashes);
        double hz = look.Trigger == "rate" ? look.RateHz : flashes / Math.Max(0.1, s.BeatPeriod);
        double t = now * Math.Max(0.1, hz);
        int flash = (int)Math.Floor(t);
        bool lit = t - flash < 0.5;
        int side = (flash / flashes) % 2;
        var a = colours[0];
        var b = colours.Length > 1 ? colours[1] : colours[0];
        var o = new Rgb[n];
        for (int i = 0; i < n; i++)
        {
            int mySide = look.Pattern == "alternate" ? i % 2 : (i < (n + 1) / 2 ? 0 : 1);
            o[i] = lit && mySide == side ? (mySide == 0 ? a : b) : Rgb.Black;
        }
        return o;
    }

    /// <summary>YARG's own Stage Kit pattern, folded onto the rig the way the original bridge did: each
    /// fixture takes the LED positions i, i+n, i+2n... and mixes the colours that are on.</summary>
    static Rgb[] StageKit(Rig rig, YargState s, int n)
    {
        var lamp = new[] { rig.Colour("red"), rig.Colour("green"), rig.Colour("blue"), rig.Colour("yellow") };
        var o = new Rgb[n];
        for (int pos = 0; pos < 8; pos++)
            for (int c = 0; c < 4; c++)
                if (s.Leds[c, pos])
                    o[pos % n] = o[pos % n].Max(lamp[c]);
        return o;
    }

    static Rgb Hue(double h)
    {
        double x = h * 6;
        int i = (int)x % 6;
        double f = x - Math.Floor(x);
        return i switch
        {
            0 => new(1, f, 0), 1 => new(1 - f, 1, 0), 2 => new(0, 1, f),
            3 => new(0, 1 - f, 1), 4 => new(f, 0, 1), _ => new(1, 0, 1 - f),
        };
    }
}
