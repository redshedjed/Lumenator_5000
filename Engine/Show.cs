using YargDmx.Input;

namespace YargDmx.Engine;

/// <summary>Renders the rig's colours for one moment: the look the current cue maps to (crossfaded from
/// the last one over the new look's <see cref="Look.Fade"/>), YARG's screen effect, then YARG's strobe on
/// top. Looks only read the YARG state and the clock; the memory is the crossfade, chaos's current draws,
/// and the recent frames that trails, fringe and choppy effects need.</summary>
public sealed class Show
{
    readonly Random _random = new();
    readonly Dictionary<string, (long Draw, Rgb[] Colours)> _chaos = new();
    string _from = "";
    double _changedAt = double.NegativeInfinity;
    readonly List<(double At, Rgb[] Frame)> _history = new();
    Rgb[] _last = Array.Empty<Rgb>();
    double _lastAt;
    long _chop = -1;

    public Rig Rig { get; set; }
    public string LookName { get; private set; } = "";

    public Show(Rig rig) => Rig = rig;

    public Rgb[] Render(YargState s, double now)
    {
        var rig = Rig;
        int n = rig.Fixtures.Count;
        var name = rig.LookNameFor(s.Cue);
        if (name != LookName)
        {
            _from = LookName;
            _changedAt = now;
            LookName = name;
        }

        var o = Draw(name, s, now, n, 0);
        double fade = rig.Looks[name].Fade;
        if (fade > 0 && now - _changedAt < fade && rig.Looks.ContainsKey(_from))
        {
            var from = Draw(_from, s, now, n, 0);
            double f = Smooth((now - _changedAt) / fade);
            for (int i = 0; i < n; i++)
                o[i] = from[i].Lerp(o[i], f);
        }

        o = Effect(rig, s, now, o);

        if (s.Strobe != Strobe.Off && rig.StrobePerBeat.TryGetValue(s.Strobe.ToString(), out var perBeat) && perBeat > 0)
        {
            bool on = BeatPos(s, now) * perBeat % 1.0 < 0.35;
            for (int i = 0; i < n; i++)
                o[i] = on ? Rgb.White : Rgb.Black;
        }
        for (int i = 0; i < n; i++)
            o[i] = o[i].Scale(Math.Clamp(rig.Master, 0, 1));
        return o;
    }

    Rgb[] Draw(string name, YargState s, double now, int n, int depth)
    {
        var rig = Rig;
        var look = rig.Looks[name];
        var colours = look.Colors.Select(rig.Colour).DefaultIfEmpty(Rgb.White).ToArray();
        return look.Type switch
        {
            "blackout" => new Rgb[n],
            "solid" => Place(look, colours, n, 0),
            "pulse" => Pulse(look, colours, s, now, n),
            "step" => Step(look, colours, s, now, n),
            "toggle" => Toggle(look, colours, s, now, n),
            "chase" => Chase(look, colours, s, now, n),
            "frets" => Frets(rig, look, s, now, n),
            "flash" => Flash(look, colours, s, now, n),
            "chaos" => Chaos(name, look, colours, s, now, n),
            "police" => Police(look, colours, s, now, n),
            "wave" => Wave(rig, look, colours, s, now, n),
            "layers" => Layers(look, s, now, n, depth),
            _ => StageKit(rig, s, n),
        };
    }

    /// <summary>The looks in <see cref="Look.Looks"/> on top of each other (the brightest of each colour).</summary>
    Rgb[] Layers(Look look, YargState s, double now, int n, int depth)
    {
        var o = new Rgb[n];
        if (depth > 4)
            return o;
        foreach (var name in look.Looks)
        {
            var layer = Draw(name, s, now, n, depth + 1);
            for (int i = 0; i < n; i++)
                o[i] = o[i].Max(layer[i]);
        }
        return o;
    }

    static double Smooth(double f)
    {
        f = Math.Clamp(f, 0, 1);
        return f * f * (3 - 2 * f);
    }

    /// <summary>Where the song is in beats: the beats YARG has sent plus the way through this one. With no
    /// beat lately it runs on at the last tempo.</summary>
    public static double BeatPos(YargState s, double now)
    {
        double period = Math.Max(0.05, s.BeatPeriod);
        double since = s.Since(Trigger.Beat, now);
        return since > 4 * period ? now / period : s.Count(Trigger.Beat) + Math.Min(since / period, 0.999);
    }

    /// <summary>Cycles completed: one per <see cref="Look.Beats"/> beats if set, else
    /// <see cref="Look.RateHz"/> per second.</summary>
    static double Phase(Look look, YargState s, double now) =>
        look.Beats > 0 ? BeatPos(s, now) / look.Beats : now * look.RateHz;

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

    static double SinceTrigger(Look look, YargState s, double now)
    {
        switch (look.Trigger)
        {
            case "rate":
                return (now * Math.Max(0.01, look.RateHz)) % 1.0 / Math.Max(0.01, look.RateHz);
            case "beats":
                double step = Math.Max(0.0625, look.Beats);
                return BeatPos(s, now) % step * Math.Max(0.05, s.BeatPeriod);
            default:
                return s.Since(Enum.Parse<Trigger>(look.Trigger, true), now);
        }
    }

    static long TriggerCount(Look look, YargState s, double now) => look.Trigger switch
    {
        "rate" => (long)Math.Floor(now * Math.Max(0.01, look.RateHz)),
        "beats" => (long)Math.Floor(BeatPos(s, now) / Math.Max(0.0625, look.Beats)),
        _ => s.Count(Enum.Parse<Trigger>(look.Trigger, true)),
    };

    /// <summary>Triggers since the current cue began.</summary>
    static long TriggersThisCue(Look look, YargState s, double now) =>
        look.Trigger is "rate" or "beats"
            ? TriggerCount(look, s, now)
            : TriggerCount(look, s, now) - s.CountsAtCue[(int)Enum.Parse<Trigger>(look.Trigger, true)];

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

    /// <summary>Colours by pattern, moving on one step per trigger with a crossfade over
    /// <see cref="Look.Decay"/>. On keyframes it follows YARG's direction: next steps on, previous steps
    /// back, first returns to the start. <see cref="Look.BeatPulse"/> dips it between beats.</summary>
    static Rgb[] Step(Look look, Rgb[] colours, YargState s, double now, int n)
    {
        bool keyframes = look.Trigger == "keyframe";
        long to = keyframes ? s.KeyframeStep : TriggerCount(look, s, now);
        long from = keyframes ? s.KeyframePrev : to - 1;
        double f = Smooth(SinceTrigger(look, s, now) / Math.Max(0.01, look.Decay));
        var a = Place(look, colours, n, (int)(from % 1_000_000));
        var b = Place(look, colours, n, (int)(to % 1_000_000));
        double level = 1 - look.BeatPulse * (1 - Math.Exp(-Math.Max(0, s.Since(Trigger.Beat, now)) / 0.25));
        for (int i = 0; i < n; i++)
            a[i] = a[i].Lerp(b[i], f).Scale(level);
        return a;
    }

    /// <summary>Colours by pattern, on when the cue starts and flipped off / on by each trigger, crossfading
    /// over <see cref="Look.Decay"/>.</summary>
    static Rgb[] Toggle(Look look, Rgb[] colours, YargState s, double now, int n)
    {
        bool on = TriggersThisCue(look, s, now) % 2 == 0;
        double f = Smooth(SinceTrigger(look, s, now) / Math.Max(0.01, look.Decay));
        double level = on ? f : 1 - f;
        var o = Place(look, colours, n, 0);
        for (int i = 0; i < n; i++)
            o[i] = o[i].Scale(level);
        return o;
    }

    /// <summary>One soft spot per colour, evenly spaced, travelling round the rig once per cycle
    /// (<see cref="Look.Beats"/> or <see cref="Look.RateHz"/>). <see cref="Look.Width"/> = spot size as a
    /// share of the rig. Bounce: back and forth instead of round (two spots then cross in the middle).</summary>
    static Rgb[] Chase(Look look, Rgb[] colours, YargState s, double now, int n)
    {
        double phase = Phase(look, s, now) * (look.Reverse ? -1 : 1);
        double w = Math.Max(0.02, look.Width);
        int k = colours.Length;
        var o = new Rgb[n];
        for (int i = 0; i < n; i++)
        {
            double u = look.Bounce ? (n == 1 ? 0.5 : i / (n - 1.0)) : (i + 0.5) / n;
            for (int j = 0; j < k; j++)
            {
                double p = phase + (double)j / k;
                p -= Math.Floor(p);
                double d;
                if (look.Bounce)
                    d = Math.Abs(u - (1 - Math.Abs(2 * p - 1)));
                else
                {
                    d = Math.Abs(u - p);
                    d = Math.Min(d, 1 - d);
                }
                o[i] = o[i].Max(colours[j].Scale(Math.Exp(-2.77 * d * d / (w * w))));   // w = width at half height
            }
        }
        return o;
    }

    /// <summary>The rig as one instrument's fretboard (drums: red, yellow, blue, green; guitar, bass and
    /// keys: green to orange), left to right. A held note lights its section and fades over
    /// <see cref="Look.Decay"/> once let go; unplayed sections sit at <see cref="Look.Floor"/>. An open
    /// note (a kick on drums) washes the whole rig.</summary>
    static Rgb[] Frets(Rig rig, Look look, YargState s, double now, int n)
    {
        var inst = Enum.Parse<Instrument>(look.Instrument, true);
        bool drums = inst == Instrument.Drums;
        // the note bits behind each section: drum pads share a section with their cymbal
        int[][] lanes = drums
            ? new[] { new[] { 1 }, new[] { 2, 5 }, new[] { 3, 6 }, new[] { 4, 7 } }
            : new[] { new[] { 1 }, new[] { 2 }, new[] { 3 }, new[] { 4 }, new[] { 5 } };
        var names = drums ? new[] { "red", "yellow", "blue", "green" } : new[] { "green", "red", "yellow", "blue", "orange" };
        var lamps = names.Select(c => Lamp(rig, c)).ToArray();
        var wash = Lamp(rig, drums ? "orange" : "purple");
        double Held(int bit) => Math.Exp(-Math.Max(0, now - s.HeldAt[(int)inst, bit]) / Math.Max(0.01, look.Decay));

        var o = new Rgb[n];
        for (int i = 0; i < n; i++)
        {
            int lane = i * lanes.Length / n;
            double level = lanes[lane].Max(Held);
            o[i] = lamps[lane].Scale(look.Floor + (1 - look.Floor) * level).Max(wash.Scale(Held(0)));
        }
        return o;
    }

    static Rgb Lamp(Rig rig, string name) =>
        rig.Palette.ContainsKey(name) ? rig.Colour(name) : name switch
        {
            "orange" => new(1, 0.3, 0), "purple" => new(0.5, 0, 1), _ => Rgb.Parse(name),
        };

    /// <summary>A flash of the look's first colour (white if none) on every trigger, the second colour (or
    /// black) underneath at the floor level. Trigger "cue": one flash as the cue starts.</summary>
    static Rgb[] Flash(Look look, Rgb[] colours, YargState s, double now, int n)
    {
        double since = SinceTrigger(look, s, now);
        double k = Math.Exp(-Math.Max(0, since) / Math.Max(0.01, look.Decay));
        var under = (colours.Length > 1 ? colours[1] : Rgb.Black).Scale(look.Floor);
        var top = under.Lerp(colours[0], k);
        return Enumerable.Repeat(top, n).ToArray();
    }

    /// <summary>A fresh random draw on every trigger: <see cref="Look.Density"/> of the fixtures lit, each
    /// in a random colour of the look (random hues if it has none).</summary>
    Rgb[] Chaos(string name, Look look, Rgb[] colours, YargState s, double now, int n)
    {
        long draw = TriggerCount(look, s, now);
        if (!_chaos.TryGetValue(name, out var c) || c.Draw != draw || c.Colours.Length != n)
        {
            c = (draw, new Rgb[n]);
            for (int i = 0; i < n; i++)
                if (_random.NextDouble() < look.Density)
                    c.Colours[i] = look.Colors.Count > 0 ? colours[_random.Next(colours.Length)] : Hue(_random.NextDouble());
            _chaos[name] = c;
        }
        double level = Envelope(look, SinceTrigger(look, s, now));
        return c.Colours.Select(x => x.Scale(level)).ToArray();
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

    /// <summary>The look's colours (a rainbow if it has none) crossfading round in a loop once per cycle
    /// (<see cref="Look.Beats"/> or <see cref="Look.RateHz"/>), each fixture behind the one to its left by
    /// its stage position, so the colour rolls across the rig. <see cref="Look.Spread"/> = cycles across
    /// the rig.</summary>
    static Rgb[] Wave(Rig rig, Look look, Rgb[] colours, YargState s, double now, int n)
    {
        double phase = Phase(look, s, now);
        var o = new Rgb[n];
        for (int i = 0; i < n; i++)
        {
            double p = phase - rig.Fixtures[i].X * look.Spread;
            p -= Math.Floor(p);
            o[i] = look.Colors.Count > 0 ? Blend(colours, p) : Hue(p);
        }
        return o;
    }

    /// <summary>A point 0-1 round a loop of colours, eased so it lingers on each one.</summary>
    static Rgb Blend(Rgb[] colours, double p)
    {
        int k = colours.Length;
        double x = p * k;
        int a = (int)x % k;
        return colours[a].Lerp(colours[(a + 1) % k], Smooth(x - Math.Floor(x)));
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

    /// <summary>YARG's screen effect applied to the whole rig (rig.json post_processing): changes to each
    /// frame, then the ones that need recent frames (fringe, trails, choppy).</summary>
    Rgb[] Effect(Rig rig, YargState s, double now, Rgb[] o)
    {
        int n = o.Length;
        rig.PostProcessing.TryGetValue(s.PostProcessing.ToString(), out var fx);
        if (fx != null)
        {
            if (fx.Mirror)
                for (int i = 0; i < n / 2; i++)
                    o[n - 1 - i] = o[i];
            for (int i = 0; i < n; i++)
            {
                var c = o[i];
                double level = c.Level;
                if (fx.Invert)
                    c = new Rgb(level - c.R, level - c.G, level - c.B);
                if (fx.Tint is { Length: > 0 })
                    c = c.Lerp(rig.Colour(fx.Tint).Scale(level), Math.Clamp(fx.Strength, 0, 1));
                if (fx.Polarize.Count > 0)
                    c = level < 0.35 ? Rgb.Black : fx.Polarize.Select(rig.Colour).MaxBy(p => Similarity(p, c));
                if (fx.Posterize)
                    c = new Rgb(c.R >= 0.5 ? 1 : 0, c.G >= 0.5 ? 1 : 0, c.B >= 0.5 ? 1 : 0);
                if (fx.Contrast > 0)
                    c = c.Lerp(new Rgb(Smooth(c.R), Smooth(c.G), Smooth(c.B)), Math.Clamp(fx.Contrast, 0, 1));
                if (fx.Lift > 0)
                    c = c.Lerp(Rgb.White.Scale(c.Level), Math.Clamp(fx.Lift, 0, 1));
                if (fx.Scanline > 0)
                {
                    double d = rig.Fixtures[i].X - (now * 0.6 % 1.4 - 0.2);      // a dark bar rolling left to right
                    c = c.Scale(1 - Math.Clamp(fx.Scanline, 0, 1) * Math.Exp(-d * d / 0.01));
                }
                if (fx.Flicker > 0)
                    c = c.Scale(1 - Math.Clamp(fx.Flicker, 0, 1) * _random.NextDouble());
                o[i] = c;
            }
        }

        _history.Add((now, (Rgb[])o.Clone()));
        _history.RemoveAll(h => now - h.At > 1.0);
        if (fx is { Fringe: > 0 })
        {
            var g = Ago(now - fx.Fringe, n);
            var b = Ago(now - 2 * fx.Fringe, n);
            for (int i = 0; i < n; i++)
                o[i] = new Rgb(o[i].R, g[i].G, b[i].B);
        }
        if (fx is { Trails: > 0 } && _last.Length == n)
        {
            double keep = Math.Exp(-Math.Max(0, now - _lastAt) / fx.Trails);
            for (int i = 0; i < n; i++)
                o[i] = o[i].Max(_last[i].Scale(keep));
        }
        if (fx is { Choppy: > 0 } && _last.Length == n)
        {
            long chop = (long)Math.Floor(now * fx.Choppy);
            if (chop == _chop)
                o = (Rgb[])_last.Clone();
            _chop = chop;
        }
        _last = (Rgb[])o.Clone();
        _lastAt = now;
        return o;
    }

    /// <summary>How alike two colours' hues are (cosine of the angle between them).</summary>
    static double Similarity(Rgb a, Rgb b) =>
        (a.R * b.R + a.G * b.G + a.B * b.B) /
        Math.Max(1e-9, Math.Sqrt(a.R * a.R + a.G * a.G + a.B * a.B) * Math.Sqrt(b.R * b.R + b.G * b.G + b.B * b.B));

    /// <summary>The effect-processed frame from about <paramref name="at"/> (the oldest kept if earlier).</summary>
    Rgb[] Ago(double at, int n)
    {
        foreach (var (t, frame) in Enumerable.Reverse(_history))
            if (t <= at && frame.Length == n)
                return frame;
        return _history.Count > 0 && _history[0].Frame.Length == n ? _history[0].Frame : new Rgb[n];
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
