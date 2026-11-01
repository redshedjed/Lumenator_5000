using System.Diagnostics;

namespace YargDmx.Input;

/// <summary>YARG's lighting cues as it sends them on the cue channel (YARG SacnInterpreter.CueEnum).</summary>
public enum Cue : byte
{
    NoCue = 0, Menu = 10, Score = 20, Intro = 30, Verse = 40, Chorus = 50, CoolLoop = 60, WarmLoop = 70,
    CoolManual = 80, WarmManual = 90, Dischord = 100, Stomp = 110, Default = 120, Harmony = 130, Frenzy = 140,
    Silhouettes = 150, SilhouettesSpotlight = 160, Searchlights = 170, Sweep = 180, BlackoutFast = 190,
    BlackoutSlow = 200, BlackoutSpotlight = 210, FlareSlow = 220, FlareFast = 230, BigRockEnding = 240,
}

public enum Strobe { Off, Slow, Medium, Fast, Fastest }

[Flags]
public enum Drum : byte
{
    None = 0, Kick = 1, Red = 2, YellowPad = 4, BluePad = 8, GreenPad = 16,
    YellowCymbal = 32, BlueCymbal = 64, GreenCymbal = 128,
}

/// <summary>YARG's screen effects as it sends them on the post-processing channel.</summary>
public enum PostProcessing : byte
{
    Default = 0, Bloom = 4, Bright = 14, Contrast = 24, Mirror = 34, PhotoNegative = 44, Posterize = 54,
    BlackAndWhite = 64, SepiaTone = 74, SilverTone = 84, ChoppyBlackAndWhite = 94, PhotoNegativeRedAndBlack = 104,
    PolarizedBlackAndWhite = 114, PolarizedRedAndBlue = 124, DesaturatedRed = 134, DesaturatedBlue = 144,
    ContrastRed = 154, ContrastGreen = 164, ContrastBlue = 174, GrainyFilm = 184, GrainyChromaticAbberation = 194,
    Scanlines = 204, ScanlinesBlackAndWhite = 214, ScanlinesBlue = 224, ScanlinesSecurity = 234,
    Trails = 244, TrailsLong = 252, TrailsDesaturated = 253, TrailsFlickery = 254, TrailsSpacey = 255,
}

/// <summary>The note channels. Guitar / bass / keys bits: 1 open, 2 green, 4 red, 8 yellow, 16 blue,
/// 32 orange; drums: see <see cref="Drum"/>.</summary>
public enum Instrument { Guitar, Bass, Keys, Drums }

public enum Trigger { Beat, Downbeat, Keyframe, Snare, Kick, Tom, Cymbal, Drums, Bonus, Guitar, Bass, Keys, Cue }

/// <summary>Where YARG puts each thing (1-based DMX channels). Defaults are YARG's own
/// (SettingsManager.Settings DMX*Channel[s]); change them here if you changed them in YARG.</summary>
public sealed class ChannelMap
{
    public int[] Dimmer { get; set; } = { 1, 9, 17, 25, 33, 41, 49, 57 };
    public int[] Red { get; set; } = { 2, 10, 18, 26, 34, 42, 50, 58 };
    public int[] Green { get; set; } = { 3, 11, 19, 27, 35, 43, 51, 59 };
    public int[] Blue { get; set; } = { 4, 12, 20, 28, 36, 44, 52, 60 };
    public int[] Yellow { get; set; } = { 5, 13, 21, 29, 37, 45, 53, 61 };
    public int Fog { get; set; } = 6;          // YARG writes every fog / strobe channel; the first is enough
    public int Strobe { get; set; } = 7;
    public int Cue { get; set; } = 8;
    public int Beatline { get; set; } = 14;
    public int Bonus { get; set; } = 15;
    public int Keyframe { get; set; } = 16;
    public int Drums { get; set; } = 22;
    public int PostProcessing { get; set; } = 23;
    public int Guitar { get; set; } = 24;
    public int Bass { get; set; } = 30;
    public int Keys { get; set; } = 32;

    /// <summary>Channels YARG's defaults use twice (its fog/strobe lists run through 14/15/22/23/30):
    /// turning fog on there reads as every drum hit at once. Move the game channels in YARG.</summary>
    public IEnumerable<string> Collisions(int[] fogChannels, int[] strobeChannels)
    {
        var game = new Dictionary<int, string>
        {
            [Cue] = "cue", [Beatline] = "beatline", [Bonus] = "bonus", [Keyframe] = "keyframe", [Drums] = "drums",
            [PostProcessing] = "post-processing", [Guitar] = "guitar", [Bass] = "bass", [Keys] = "keys",
        };
        foreach (var (ch, what) in fogChannels.Select(c => (c, "fog")).Concat(strobeChannels.Select(c => (c, "strobe"))))
            if (game.TryGetValue(ch, out var g))
                yield return $"channel {ch}: YARG {what} and {g}";
    }

    public static readonly int[] YargFog = { 6, 14, 22, 30, 38, 46, 54, 62 };
    public static readonly int[] YargStrobe = { 7, 15, 23, 31, 39, 47, 55, 63 };
}

/// <summary>What YARG last said, plus a count and time for every kind of trigger. Triggers are pulses
/// (YARG holds them DMXPulseDuration ms, 60 by default), so they're counted on the rising edge: two of
/// the same within one pulse read as one.</summary>
public sealed class YargState
{
    public bool[,] Leds = new bool[4, 8];     // [red, green, blue, yellow][position]
    public Cue Cue = Cue.NoCue;
    public Strobe Strobe;
    public bool Fog;
    public PostProcessing PostProcessing;
    public Drum DrumsHeld;
    /// <summary>The note bits each <see cref="Instrument"/> is holding, and when each bit was last held.</summary>
    public byte[] Held = new byte[4];
    public double[,] HeldAt = new double[4, 8];
    /// <summary>Where the keyframes have walked to: Next +1, Previous -1, First back to 0.</summary>
    public int KeyframeStep, KeyframePrev;
    public long[] Counts = new long[Enum.GetValues<Trigger>().Length];
    public double[] LastAt = Enumerable.Repeat(double.NegativeInfinity, Enum.GetValues<Trigger>().Length).ToArray();
    public double CueSince;
    /// <summary><see cref="Counts"/> as they were when the current cue began.</summary>
    public long[] CountsAtCue = new long[Enum.GetValues<Trigger>().Length];
    public double BeatPeriod = 60.0 / 120;       // seconds, from the beatline
    public double LastFrameAt = double.NegativeInfinity;

    public long Count(Trigger t) => Counts[(int)t];
    public double Since(Trigger t, double now) => now - LastAt[(int)t];

    public YargState()
    {
        for (int i = 0; i < 4; i++)
            for (int bit = 0; bit < 8; bit++)
                HeldAt[i, bit] = double.NegativeInfinity;
    }

    public YargState Clone()
    {
        var c = (YargState)MemberwiseClone();
        c.Leds = (bool[,])Leds.Clone();
        c.Counts = (long[])Counts.Clone();
        c.CountsAtCue = (long[])CountsAtCue.Clone();
        c.LastAt = (double[])LastAt.Clone();
        c.Held = (byte[])Held.Clone();
        c.HeldAt = (double[,])HeldAt.Clone();
        return c;
    }
}

/// <summary>Turns YARG's universe frames into a <see cref="YargState"/>.</summary>
public sealed class YargDecoder
{
    readonly object _lock = new();
    readonly Stopwatch _clock;
    YargState _state = new();
    byte[] _prev = new byte[512];
    double _lastBeat = double.NegativeInfinity;

    public ChannelMap Map { get; set; }

    public YargDecoder(ChannelMap map, Stopwatch clock)
    {
        Map = map;
        _clock = clock;
    }

    public YargState Snapshot()
    {
        lock (_lock) return _state.Clone();
    }

    static byte At(byte[] f, int ch) => ch is >= 1 and <= 512 ? f[ch - 1] : (byte)0;

    public void OnFrame(byte[] f)
    {
        double now = _clock.Elapsed.TotalSeconds;
        lock (_lock)
        {
            var s = _state;
            var m = Map;
            int[][] colours = { m.Red, m.Green, m.Blue, m.Yellow };
            for (int c = 0; c < 4; c++)
                for (int i = 0; i < 8; i++)
                    s.Leds[c, i] = i < colours[c].Length && At(f, colours[c][i]) != 0;
            s.Fog = At(f, m.Fog) != 0;
            s.Strobe = At(f, m.Strobe) switch
            {
                0 => Strobe.Off, <= 64 => Strobe.Slow, <= 127 => Strobe.Medium, <= 191 => Strobe.Fast, _ => Strobe.Fastest,
            };
            var cue = At(f, m.Cue);
            if (Enum.IsDefined(typeof(Cue), cue) && (Cue)cue != s.Cue)
            {
                s.Cue = (Cue)cue;
                s.CueSince = now;
                Fire(s, Trigger.Cue, now);
                s.CountsAtCue = (long[])s.Counts.Clone();
            }
            if (At(f, m.PostProcessing) is var pp && Enum.IsDefined(typeof(PostProcessing), pp))
                s.PostProcessing = (PostProcessing)pp;

            // pulses: only values YARG really sends count, so a fog/strobe channel sitting on the same
            // address (YARG's defaults) can't fire them
            var beat = Rising(f, m.Beatline);
            if (beat is 1 or 11)
            {
                if (now - _lastBeat is > 0.15 and < 2.0)
                    s.BeatPeriod = 0.7 * s.BeatPeriod + 0.3 * (now - _lastBeat);
                _lastBeat = now;
                Fire(s, Trigger.Beat, now);
                if (beat == 1)          // BeatlineType.Measure: the bar line
                    Fire(s, Trigger.Downbeat, now);
            }
            if (Rising(f, m.Keyframe) is var kf and (3 or 13 or 23))
            {
                s.KeyframePrev = s.KeyframeStep;
                s.KeyframeStep = kf switch { 3 => s.KeyframeStep + 1, 13 => s.KeyframeStep - 1, _ => 0 };
                Fire(s, Trigger.Keyframe, now);
            }
            if (Rising(f, m.Bonus) == 2)
                Fire(s, Trigger.Bonus, now);

            var drums = (Drum)At(f, m.Drums);
            var hit = drums & ~(Drum)At(_prev, m.Drums);
            s.DrumsHeld = drums;
            if (hit.HasFlag(Drum.Red)) Fire(s, Trigger.Snare, now);
            if (hit.HasFlag(Drum.Kick)) Fire(s, Trigger.Kick, now);
            if ((hit & (Drum.YellowPad | Drum.BluePad | Drum.GreenPad)) != 0) Fire(s, Trigger.Tom, now);
            if ((hit & (Drum.YellowCymbal | Drum.BlueCymbal | Drum.GreenCymbal)) != 0) Fire(s, Trigger.Cymbal, now);
            if (hit != 0) Fire(s, Trigger.Drums, now);
            if (At(f, m.Guitar) is var g and not 0 && g != At(_prev, m.Guitar)) Fire(s, Trigger.Guitar, now);
            if (At(f, m.Bass) is var b and not 0 && b != At(_prev, m.Bass)) Fire(s, Trigger.Bass, now);
            if (At(f, m.Keys) is var k and not 0 && k != At(_prev, m.Keys)) Fire(s, Trigger.Keys, now);

            int[] noteChannels = { m.Guitar, m.Bass, m.Keys, m.Drums };
            for (int i = 0; i < 4; i++)
            {
                s.Held[i] = At(f, noteChannels[i]);
                for (int bit = 0; bit < 8; bit++)
                    if ((s.Held[i] & (1 << bit)) != 0)
                        s.HeldAt[i, bit] = now;
            }

            s.LastFrameAt = now;
            _prev = f;
        }
    }

    /// <summary>The channel's value if it just changed to non-zero, else 0.</summary>
    byte Rising(byte[] f, int ch)
    {
        var v = At(f, ch);
        return v != 0 && v != At(_prev, ch) ? v : (byte)0;
    }

    static void Fire(YargState s, Trigger t, double now)
    {
        s.Counts[(int)t]++;
        s.LastAt[(int)t] = now;
    }
}
