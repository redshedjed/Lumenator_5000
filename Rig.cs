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
    public int Width => new[] { Dimmer ?? 0, Red, Green, Blue, White ?? 0 }
        .Concat(Fixed.Keys.Select(int.Parse)).Max();
}

public sealed class Fixture
{
    public string Name { get; set; } = "";
    public int Address { get; set; } = 1;
    public string Profile { get; set; } = "par7";
    /// <summary>Where it sits on the visualizer stage, 0 (left) to 1 (right) and 0 (back) to 1 (front).</summary>
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = 0.5;
}

/// <summary>One look: what the lights do while a cue is up. Unused fields are ignored per type.</summary>
public sealed class Look
{
    /// <summary>stagekit | solid | pulse | flash | chaos | police | blackout</summary>
    public string Type { get; set; } = "stagekit";
    public List<string> Colors { get; set; } = new();
    /// <summary>How colours sit on the fixtures: alternate (1 2 1 2) | halves (1 1 2 2) | all | chase.</summary>
    public string Pattern { get; set; } = "alternate";
    /// <summary>What drives it: beat | downbeat | keyframe | snare | kick | cymbal | bonus | guitar | bass | keys | rate.</summary>
    public string Trigger { get; set; } = "beat";
    public double RateHz { get; set; } = 4;
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
}

public sealed class OutputConfig
{
    public int Fps { get; set; } = 40;
    /// <summary>FTDI USB-DMX ("Open DMX") cable: true to drive it.</summary>
    public bool Ftdi { get; set; }
    /// <summary>Also send the result as sACN on this universe (0 = off). Not YARG's own universe.</summary>
    public ushort SacnUniverse { get; set; }
    public int VisualizerPort { get; set; } = 8080;
}

public sealed class Rig
{
    public ushort Universe { get; set; } = 1;
    public ChannelMap Channels { get; set; } = new();
    public OutputConfig Output { get; set; } = new();
    public double Master { get; set; } = 1.0;
    /// <summary>Follow YARG's strobe cues (white strobe over everything) at these rates (Hz).</summary>
    public Dictionary<string, double> StrobeHz { get; set; } = new() { ["Slow"] = 4, ["Medium"] = 7, ["Fast"] = 10, ["Fastest"] = 15 };
    public Dictionary<string, Profile> Profiles { get; set; } = new();
    public List<Fixture> Fixtures { get; set; } = new();
    public Dictionary<string, string> Palette { get; set; } = new();
    public Dictionary<string, Look> Looks { get; set; } = new();
    /// <summary>YARG cue name -> look name; "*" for every cue not listed.</summary>
    public Dictionary<string, string> Cues { get; set; } = new();

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,   // a typo'd key is an error, not silence
    };

    public static Rig Load(string path)
    {
        var rig = JsonSerializer.Deserialize<Rig>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("empty rig file");
        rig.Validate();
        return rig;
    }

    public Rgb Colour(string nameOrHex) =>
        Rgb.Parse(Palette.TryGetValue(nameOrHex, out var hex) ? hex : nameOrHex);

    public Look LookFor(Cue cue) =>
        Looks[Cues.TryGetValue(cue.ToString(), out var name) ? name : Cues.GetValueOrDefault("*", "stagekit")];

    void Validate()
    {
        if (Fixtures.Count == 0)
            throw new InvalidDataException("no fixtures");
        Looks.TryAdd("stagekit", new Look());
        for (int i = 0; i < Fixtures.Count; i++)
        {
            var f = Fixtures[i];
            if (!Profiles.TryGetValue(f.Profile, out var p))
                throw new InvalidDataException($"fixture {f.Name}: no profile \"{f.Profile}\"");
            if (f.Address < 1 || f.Address + p.Width - 1 > 512)
                throw new InvalidDataException($"fixture {f.Name}: address {f.Address} doesn't fit");
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
            if (look.Trigger != "rate" && !Enum.TryParse<Trigger>(look.Trigger, true, out _))
                throw new InvalidDataException($"look {name}: trigger \"{look.Trigger}\"");
        }
    }
}
