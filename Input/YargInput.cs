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
    public double BeatPeriod = 60.0 / 120;       // seconds, from the beatline (or the data stream's tempo)
    public double LastFrameAt = double.NegativeInfinity;
    /// <summary>"sACN", or "data stream" while YARG's UDP data stream is arriving (it then drives
    /// everything except the Stage Kit LEDs, which only sACN has).</summary>
    public string Source = "sACN";

    // only from the data stream
    /// <summary>The chart's tempo, 0 if not known.</summary>
    public double Bpm;
    public bool Paused;
    public DataStreamPacket.Scenes Scene;
    /// <summary>Performer bits (guitar 1, bass 2, drums 4, vocals 8, keys 16) in a spotlight / singing along.</summary>
    public byte Spotlight, Singalong;
    /// <summary>YARG.Core CameraCutSubject index of the current camera cut.</summary>
    public byte CameraSubject;
    /// <summary>The lead vocal's pitch (MIDI note number), 0 between notes.</summary>
    public float Vocal;

    // only from REAPER
    /// <summary>After a scrub or a stop: dark until the next cue, and keyframes do nothing till then.</summary>
    public bool Waiting;
    /// <summary>REAPER is paused: hold the lights as they are.</summary>
    public bool Frozen;

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
            s.LastFrameAt = now;
            if (StreamActive(now))
            {
                _prev = f;                              // the data stream has the rest
                return;
            }
            if (s.Source != "sACN")                     // the data stream stopped: forget what only it knew
            {
                s.Source = "sACN";
                (s.Bpm, s.Paused, s.Spotlight, s.Singalong, s.Vocal) = (0, false, 0, 0, 0);
            }
            s.Fog = At(f, m.Fog) != 0;
            s.Strobe = At(f, m.Strobe) switch
            {
                0 => Strobe.Off, <= 64 => Strobe.Slow, <= 127 => Strobe.Medium, <= 191 => Strobe.Fast, _ => Strobe.Fastest,
            };
            var cue = At(f, m.Cue);
            if (Enum.IsDefined(typeof(Cue), cue))
                SetCue(s, (Cue)cue, now);
            if (At(f, m.PostProcessing) is var pp && Enum.IsDefined(typeof(PostProcessing), pp))
                s.PostProcessing = (PostProcessing)pp;

            // pulses: only values YARG really sends count, so a fog/strobe channel sitting on the same
            // address (YARG's defaults) can't fire them
            var beat = Rising(f, m.Beatline);
            if (beat is 1 or 11)
                Beat(s, measure: beat == 1, now);
            if (Rising(f, m.Keyframe) is var kf and (3 or 13 or 23))
                Keyframe(s, kf switch { 3 => 1, 13 => -1, _ => 0 }, now);
            if (Rising(f, m.Bonus) == 2)
                Fire(s, Trigger.Bonus, now);

            Notes(s, At(f, m.Guitar), At(f, m.Bass), At(f, m.Keys), At(f, m.Drums), now);
            _prev = f;
        }
    }

    // --- YARG's UDP data stream: while it's arriving it drives everything but the Stage Kit LEDs
    double _streamAt = double.NegativeInfinity;
    byte _lastLighting = byte.MaxValue, _lastSection = byte.MaxValue;
    DataStreamPacket.Scenes? _lastScene;

    bool StreamActive(double now) => now - _streamAt < 1.0;

    public void OnDataStream(DataStreamPacket p)
    {
        double now = _clock.Elapsed.TotalSeconds;
        lock (_lock)
        {
            var s = _state;
            if (!StreamActive(now))
                _lastScene = null;                      // (re)joining: sACN may have moved the cue meanwhile
            _streamAt = now;
            s.LastFrameAt = now;
            s.Source = "data stream";
            s.Fog = p.Fog;
            s.Strobe = DataStreamPacket.StrobeFor(p.Strobe);
            s.PostProcessing = DataStreamPacket.PostProcessingFor(p.PostProcessing);
            s.Paused = p.IsPaused;
            s.Scene = (DataStreamPacket.Scenes)p.Scene;
            s.Spotlight = p.Spotlight;
            s.Singalong = p.Singalong;
            s.CameraSubject = p.CameraSubject;
            s.Vocal = p.Vocal;
            if (p.Bpm is > 20 and < 400)
            {
                s.Bpm = p.Bpm;
                s.BeatPeriod = 60.0 / p.Bpm;
            }

            // the cue: the screen outside a song; in one, the latest lighting cue or song section (YARG
            // sends Verse and Chorus as the section, not the cue). A song that starts while we listen is
            // NoCue until its chart says otherwise (the stream still holds the last song's cue); one
            // already playing when we start listening takes the cue it's on.
            bool inSong = s.Scene is DataStreamPacket.Scenes.Gameplay or DataStreamPacket.Scenes.Practice;
            if (s.Scene != _lastScene)
            {
                bool joinedMidSong = _lastScene == null && inSong;
                _lastScene = s.Scene;
                (_lastLighting, _lastSection) = joinedMidSong ? (byte.MaxValue, byte.MaxValue) : (p.LightingCue, p.SongSection);
                SetCue(s, s.Scene switch
                {
                    DataStreamPacket.Scenes.Menu or DataStreamPacket.Scenes.Calibration => Cue.Menu,
                    DataStreamPacket.Scenes.Score => Cue.Score,
                    _ => Cue.NoCue,
                }, now);
            }
            if (inSong)
            {
                if (p.LightingCue != _lastLighting)
                {
                    _lastLighting = p.LightingCue;
                    if (DataStreamPacket.CueFor(p.LightingCue) is { } cue)
                        SetCue(s, cue, now);
                }
                if (p.SongSection != _lastSection)
                {
                    _lastSection = p.SongSection;
                    if (DataStreamPacket.CueFor(p.SongSection) is Cue.Verse or Cue.Chorus)
                        SetCue(s, DataStreamPacket.CueFor(p.SongSection)!.Value, now);
                }
            }

            if (p.Beat is 0 or 1)                       // a measure or strong beatline (weak ones: not on sACN either)
                Beat(s, measure: p.Beat == 0, now);
            switch (DataStreamPacket.Lighting(p.Keyframe))
            {
                case "KeyframeNext": Keyframe(s, 1, now); break;
                case "KeyframePrevious": Keyframe(s, -1, now); break;
                case "KeyframeFirst": Keyframe(s, 0, now); break;
            }
            if (p.Bonus)
                Fire(s, Trigger.Bonus, now);
            Notes(s, p.Guitar, p.Bass, p.Keys, p.Drums, now);
        }
    }

    // --- a chart played from REAPER (see ReaperPlayer)

    /// <summary>One step of the song: <paramref name="reset"/> if the cursor moved or it stopped, then what
    /// the chart says in order. <paramref name="struck"/> are the notes that began this step, so a note
    /// right after the same note still counts as a hit.</summary>
    public void OnChart(double now, string source, bool reset, bool paused, double bpm,
                        IReadOnlyList<ChartEvent> events, byte[] held, byte[] struck)
    {
        lock (_lock)
        {
            var s = _state;
            s.LastFrameAt = now;
            s.Source = source;
            s.Paused = s.Frozen = paused;
            if (bpm > 0)
            {
                s.Bpm = bpm;
                s.BeatPeriod = 60.0 / bpm;
            }
            if (reset)
            {
                s.Waiting = true;
                s.Cue = Cue.NoCue;
                s.CueSince = now;
                (s.Strobe, s.Fog, s.PostProcessing) = (Strobe.Off, false, PostProcessing.Default);
                (s.KeyframeStep, s.KeyframePrev) = (0, 0);
            }
            foreach (var e in events)
                switch (e.Kind)
                {
                    case ChartKind.Cue:
                        s.Waiting = false;
                        s.Strobe = Strobe.Off;       // as YARG does: any new cue ends a strobe
                        SetCue(s, (Cue)e.Value, now);
                        break;
                    case ChartKind.Strobe: s.Strobe = (Strobe)e.Value; break;
                    case ChartKind.Fog: s.Fog = e.Value != 0; break;
                    case ChartKind.Effect: s.PostProcessing = (PostProcessing)e.Value; break;
                    case ChartKind.Beat: Beat(s, measure: e.Value == 1, now); break;
                    case ChartKind.Keyframe:
                        if (!s.Waiting)
                            Keyframe(s, e.Value, now);
                        break;
                }
            byte Without(int i) => (byte)(held[i] & ~struck[i]);
            int g = (int)Instrument.Guitar, b = (int)Instrument.Bass, k = (int)Instrument.Keys, d = (int)Instrument.Drums;
            Notes(s, Without(g), Without(b), Without(k), Without(d), now);
            Notes(s, held[g], held[b], held[k], held[d], now);
        }
    }

    // --- shared by the inputs

    static void SetCue(YargState s, Cue cue, double now)
    {
        if (cue == s.Cue)
            return;
        s.Cue = cue;
        s.CueSince = now;
        Fire(s, Trigger.Cue, now);
        s.CountsAtCue = (long[])s.Counts.Clone();
    }

    void Beat(YargState s, bool measure, double now)
    {
        // sACN has no tempo, so it's measured from the beatlines; the data stream says it
        if (s.Bpm == 0 && now - _lastBeat is > 0.15 and < 2.0)
            s.BeatPeriod = 0.7 * s.BeatPeriod + 0.3 * (now - _lastBeat);
        _lastBeat = now;
        Fire(s, Trigger.Beat, now);
        if (measure)                // BeatlineType.Measure: the bar line
            Fire(s, Trigger.Downbeat, now);
    }

    static void Keyframe(YargState s, int step, double now)
    {
        s.KeyframePrev = s.KeyframeStep;
        s.KeyframeStep = step == 0 ? 0 : s.KeyframeStep + step;
        Fire(s, Trigger.Keyframe, now);
    }

    /// <summary>Notes held now (guitar / bass / keys / drums bits); hits are the bits that weren't held before.</summary>
    static void Notes(YargState s, byte guitar, byte bass, byte keys, byte drumBits, double now)
    {
        var drums = (Drum)drumBits;
        var hit = drums & ~(Drum)s.Held[(int)Instrument.Drums];
        s.DrumsHeld = drums;
        if (hit.HasFlag(Drum.Red)) Fire(s, Trigger.Snare, now);
        if (hit.HasFlag(Drum.Kick)) Fire(s, Trigger.Kick, now);
        if ((hit & (Drum.YellowPad | Drum.BluePad | Drum.GreenPad)) != 0) Fire(s, Trigger.Tom, now);
        if ((hit & (Drum.YellowCymbal | Drum.BlueCymbal | Drum.GreenCymbal)) != 0) Fire(s, Trigger.Cymbal, now);
        if (hit != 0) Fire(s, Trigger.Drums, now);
        if (guitar != 0 && guitar != s.Held[(int)Instrument.Guitar]) Fire(s, Trigger.Guitar, now);
        if (bass != 0 && bass != s.Held[(int)Instrument.Bass]) Fire(s, Trigger.Bass, now);
        if (keys != 0 && keys != s.Held[(int)Instrument.Keys]) Fire(s, Trigger.Keys, now);

        byte[] held = { guitar, bass, keys, drumBits };      // Instrument order
        for (int i = 0; i < 4; i++)
        {
            s.Held[i] = held[i];
            for (int bit = 0; bit < 8; bit++)
                if ((held[i] & (1 << bit)) != 0)
                    s.HeldAt[i, bit] = now;
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
