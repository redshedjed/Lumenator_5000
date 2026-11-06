using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using YargDmx.Input;

namespace YargDmx;

public readonly record struct Rgb(double R, double G, double B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(1, 1, 1);

    public Rgb Scale(double k) => new(R * k, G * k, B * k);
    public Rgb Max(Rgb o) => new(Math.Max(R, o.R), Math.Max(G, o.G), Math.Max(B, o.B));
    public Rgb Lerp(Rgb to, double f) => new(R + (to.R - R) * f, G + (to.G - G) * f, B + (to.B - B) * f);
    public double Level => Math.Max(R, Math.Max(G, B));

    public static Rgb Parse(string s)
    {
        s = s.TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"colour \"{s}\" isn't #rrggbb");
        return new(((v >> 16) & 255) / 255.0, ((v >> 8) & 255) / 255.0, (v & 255) / 255.0);
    }
}

/// <summary>How a fixture's DMX channels are laid out (offsets from its address, 1 = the address).</summary>
public sealed class Profile
{
    public int? Dimmer { get; set; }
    public int Red { get; set; } = 1;
    public int Green { get; set; } = 2;
    public int Blue { get; set; } = 3;
    public int? White { get; set; }
    /// <summary>Channels held at a fixed value, e.g. a mode channel that must stay 0.</summary>
    public Dictionary<string, byte> Fixed { get; set; } = new();
    /// <summary>Channels that repeat a colour (red | green | blue | white), e.g. a second bank of green LEDs.</summary>
    public Dictionary<string, string> Copy { get; set; } = new();
    public int Width => new[] { Dimmer ?? 0, Red, Green, Blue, White ?? 0 }
        .Concat(Fixed.Keys.Select(int.Parse)).Concat(Copy.Keys.Select(int.Parse)).Max();
}

public sealed class Fixture
{
    public string Name { get; set; } = "";
    public int Address { get; set; } = 1;
    public string Profile { get; set; } = "par7";
    /// <summary>This light's brightness, 0-1, on top of the master: evens out lights that are brighter than others.</summary>
    public double Level { get; set; } = 1.0;
    /// <summary>Where it sits on the visualizer stage, 0 (left) to 1 (right) and 0 (back) to 1 (front).</summary>
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = 0.5;
}

/// <summary>One look: what the lights do while a cue is up. Unused fields are ignored per type.</summary>
public sealed class Look
{
    /// <summary>solid | pulse | flash | step | toggle | chase | chaos | police | wave | frets | layers |
    /// stagekit | blackout</summary>
    public string Type { get; set; } = "stagekit";
    public List<string> Colors { get; set; } = new();
    /// <summary>How colours sit on the fixtures: alternate (1 2 1 2) | halves (1 1 2 2) | all | chase.</summary>
    public string Pattern { get; set; } = "alternate";
    /// <summary>What drives it: beat | downbeat | keyframe | snare | kick | tom | cymbal | drums | bonus | guitar |
    /// bass | keys | cue (once, as the cue starts) | rate (rate_hz) | beats (every <see cref="Beats"/> beats).</summary>
    public string Trigger { get; set; } = "beat";
    public double RateHz { get; set; } = 4;
    /// <summary>Tempo-locked timing in beats: the step for trigger "beats" (0.25 = 16th notes), or one
    /// cycle of a wave / chase (instead of <see cref="RateHz"/>).</summary>
    public double Beats { get; set; }
    /// <summary>Seconds to crossfade from the previous look when this one comes up.</summary>
    public double Fade { get; set; } = 0.3;
    /// <summary>pulse / flash: seconds for a hit to fade to <see cref="Floor"/>.</summary>
    public double Decay { get; set; } = 0.25;
    /// <summary>Level between hits, 0-1.</summary>
    public double Floor { get; set; } = 0.25;
    /// <summary>pulse: a downbeat hits this much harder than other beats (1 = the same).</summary>
    public double Accent { get; set; } = 1.0;
    /// <summary>chaos: share of fixtures lit per hit.</summary>
    public double Density { get; set; } = 0.6;
    /// <summary>police: flashes per side before it swaps.</summary>
    public int Flashes { get; set; } = 2;
    /// <summary>wave: how many trips round the colours fit across the rig at once.</summary>
    public double Spread { get; set; } = 0.5;
    /// <summary>frets: whose notes, guitar | bass | keys | drums.</summary>
    public string Instrument { get; set; } = "drums";
    /// <summary>chase: spot size as a share of the rig.</summary>
    public double Width { get; set; } = 0.25;
    /// <summary>chase: back and forth instead of round; reverse: right to left.</summary>
    public bool Bounce { get; set; }
    public bool Reverse { get; set; }
    /// <summary>step: how far it dips between beats, 0-1 (0 = steady).</summary>
    public double BeatPulse { get; set; }
    /// <summary>layers: the looks drawn on top of each other.</summary>
    public List<string> Looks { get; set; } = new();
}

/// <summary>What a YARG screen effect does to the whole rig. In rig.json, a plain colour is short for a
/// full tint in that colour.</summary>
public sealed class PostEffect
{
    /// <summary>Turn the rig toward this colour at each light's brightness ("white" = black and white).</summary>
    public string? Tint { get; set; }
    /// <summary>How far toward the tint, 0-1.</summary>
    public double Strength { get; set; } = 1;
    /// <summary>Push levels apart (dim darker, bright brighter), 0-1.</summary>
    public double Contrast { get; set; }
    /// <summary>Lift lit colours toward white, 0-1.</summary>
    public double Lift { get; set; }
    /// <summary>The right half copies the left, reversed.</summary>
    public bool Mirror { get; set; }
    /// <summary>Each colour becomes its complement (red -> cyan) at the same brightness.</summary>
    public bool Invert { get; set; }
    /// <summary>Every colour channel snaps to full or off.</summary>
    public bool Posterize { get; set; }
    /// <summary>Each light becomes whichever of these colours it's nearest, at full, or off if it's dim.</summary>
    public List<string> Polarize { get; set; } = new();
    /// <summary>Seconds lights linger after they drop (a visual echo).</summary>
    public double Trails { get; set; }
    /// <summary>Random flicker depth, 0-1 (film grain).</summary>
    public double Flicker { get; set; }
    /// <summary>Update the rig only this many times a second (0 = every frame).</summary>
    public double Choppy { get; set; }
    /// <summary>A dark bar rolling across the rig, depth 0-1.</summary>
    public double Scanline { get; set; }
    /// <summary>Green lags red by this many seconds and blue lags by twice that: colour fringes on hits.</summary>
    public double Fringe { get; set; }
}

/// <summary>Reads a <see cref="PostEffect"/> from an object or from a plain colour (a full tint).</summary>
sealed class PostEffectConverter : JsonConverter<PostEffect>
{
    JsonSerializerOptions? _inner;

    public override PostEffect Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new PostEffect { Tint = reader.GetString() };
        if (_inner == null)
        {
            var inner = new JsonSerializerOptions(options);
            inner.Converters.Remove(this);
            _inner = inner;
        }
        return JsonSerializer.Deserialize<PostEffect>(ref reader, _inner) ?? new PostEffect();
    }

    public override void Write(Utf8JsonWriter writer, PostEffect value, JsonSerializerOptions options) =>
        throw new NotSupportedException();
}

public sealed class OutputConfig
{
    public int Fps { get; set; } = 40;
    /// <summary>FTDI USB-DMX ("Open DMX") cable to drive, by serial number ("" = none).</summary>
    public string UsbAdapter { get; set; } = "";
    /// <summary>Older setting: true drives the first FTDI cable found (when <see cref="UsbAdapter"/> is "").</summary>
    public bool Ftdi { get; set; }
    /// <summary>Draw the result on the visualizer page (off: the page still runs, for these settings).</summary>
    public bool Visualizer { get; set; } = true;
    /// <summary>Also send the result as sACN on this universe (0 = off). Not YARG's own universe.</summary>
    public ushort SacnUniverse { get; set; }
    public int VisualizerPort { get; set; } = 8080;
    /// <summary>A DMX channel set to 255 while YARG's fog is on, for a fog machine (0 = none).</summary>
    public int FogChannel { get; set; }
}

public sealed class Rig
{
    /// <summary>Where the show comes from: yarg | sim (the demo song) | reaper (a chart being edited).</summary>
    public string Input { get; set; } = "yarg";
    public ushort Universe { get; set; } = 1;
    public ChannelMap Channels { get; set; } = new();
    public OutputConfig Output { get; set; } = new();
    public double Master { get; set; } = 1.0;
    /// <summary>Verse and Chorus get their own looks. Off (the default) they change nothing and the last cue runs
    /// on, as on YARG's Stage Kit. On, the data stream can't show a return to the cue it still holds, so
    /// Frenzy, Verse, Frenzy stays on Verse.</summary>
    public bool VerseChorusLooks { get; set; }
    /// <summary>Follow YARG's strobe cues (white strobe over everything), this many flashes per beat.</summary>
    public Dictionary<string, double> StrobePerBeat { get; set; } = new() { ["Slow"] = 2, ["Medium"] = 3, ["Fast"] = 4, ["Fastest"] = 6 };
    public Dictionary<string, Profile> Profiles { get; set; } = new();
    public List<Fixture> Fixtures { get; set; } = new();
    public Dictionary<string, string> Palette { get; set; } = new();
    public Dictionary<string, Look> Looks { get; set; } = new();
    /// <summary>YARG cue name -> look name; "*" for every cue not listed.</summary>
    public Dictionary<string, string> Cues { get; set; } = new();
    /// <summary>YARG post-processing effect -> what it does to the whole rig while it's on.</summary>
    public Dictionary<string, PostEffect> PostProcessing { get; set; } = new();

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,   // a typo'd key is an error, not silence
        Converters = { new PostEffectConverter() },
    };

    public static Rig Load(string path) => Parse(File.ReadAllText(path));

    public static Rig Parse(string json)
    {
        var rig = JsonSerializer.Deserialize<Rig>(json, Json) ?? throw new InvalidDataException("empty rig file");
        rig.Validate();
        return rig;
    }

    public Rgb Colour(string nameOrHex) =>
        Rgb.Parse(Palette.TryGetValue(nameOrHex, out var hex) ? hex : nameOrHex);

    public string LookNameFor(Cue cue) =>
        Cues.TryGetValue(cue.ToString(), out var name) ? name : Cues.GetValueOrDefault("*", "stagekit");

    void Validate()
    {
        if (Fixtures.Count == 0)
            throw new InvalidDataException("no fixtures");
        if (Input is not ("yarg" or "sim" or "reaper"))
            throw new InvalidDataException($"input \"{Input}\" (yarg, sim or reaper)");
        Looks.TryAdd("stagekit", new Look());
        foreach (var (name, p) in Profiles)
            foreach (var (ch, colour) in p.Copy)
                if (!int.TryParse(ch, out _) || colour is not ("red" or "green" or "blue" or "white"))
                    throw new InvalidDataException($"profile {name}: copy \"{ch}\": \"{colour}\" (red, green, blue or white)");
        for (int i = 0; i < Fixtures.Count; i++)
        {
            var f = Fixtures[i];
            if (!Profiles.TryGetValue(f.Profile, out var p))
                throw new InvalidDataException($"fixture {f.Name}: no profile \"{f.Profile}\"");
            if (f.Address < 1 || f.Address + p.Width - 1 > 512)
                throw new InvalidDataException($"fixture {f.Name}: address {f.Address} doesn't fit");
            if (f.Level is < 0 or > 1 || double.IsNaN(f.Level))
                throw new InvalidDataException($"fixture {f.Name}: level {f.Level} (0 to 1)");
            if (double.IsNaN(f.X))
                f.X = Fixtures.Count == 1 ? 0.5 : 0.1 + 0.8 * i / (Fixtures.Count - 1);
        }
        foreach (var (cue, look) in Cues)
        {
            if (cue != "*" && !Enum.TryParse<Cue>(cue, out _))
                throw new InvalidDataException($"cues: \"{cue}\" isn't a YARG cue ({string.Join(", ", Enum.GetNames<Cue>())})");
            if (!Looks.ContainsKey(look))
                throw new InvalidDataException($"cues: {cue} -> no look \"{look}\"");
        }
        foreach (var (name, look) in Looks)
        {
            foreach (var c in look.Colors)
                _ = Colour(c);
            if (look.Trigger is not ("rate" or "beats") && !Enum.TryParse<Trigger>(look.Trigger, true, out _))
                throw new InvalidDataException($"look {name}: trigger \"{look.Trigger}\"");
            if (look.Trigger == "beats" && look.Beats <= 0)
                throw new InvalidDataException($"look {name}: trigger \"beats\" needs beats (e.g. 0.25 for 16th notes)");
            if (!Enum.TryParse<Instrument>(look.Instrument, true, out _))
                throw new InvalidDataException($"look {name}: instrument \"{look.Instrument}\"");
            foreach (var layer in look.Looks)
                if (!Looks.ContainsKey(layer) || layer == name)
                    throw new InvalidDataException($"look {name}: no look \"{layer}\" to layer");
        }
        foreach (var (effect, fx) in PostProcessing)
        {
            if (!Enum.TryParse<Input.PostProcessing>(effect, out _))
                throw new InvalidDataException($"post_processing: \"{effect}\" isn't a YARG effect ({string.Join(", ", Enum.GetNames<Input.PostProcessing>())})");
            if (fx.Tint != null)
                _ = Colour(fx.Tint);
            foreach (var c in fx.Polarize)
                _ = Colour(c);
        }
    }
}
