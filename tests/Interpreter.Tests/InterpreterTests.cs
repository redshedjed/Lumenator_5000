using System.Diagnostics;
using YargDmx;
using YargDmx.Engine;
using YargDmx.Input;
using YargDmx.Output;

namespace Interpreter.Tests;

public class E131Tests
{
    [Fact]
    public void BuildThenParseRoundTrips()
    {
        var slots = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
        var p = E131.Build(7, slots, 3, Guid.NewGuid(), "test");
        Assert.True(E131.TryParse(p, out var u, out var back));
        Assert.Equal(7, u);
        Assert.Equal(slots, back.ToArray());
    }

    [Fact]
    public void RejectsOtherPackets()
    {
        var p = E131.Build(1, new byte[512], 0, Guid.NewGuid(), "test");
        p[125] = 0xdd;                                  // per-address priority, not DMX levels
        Assert.False(E131.TryParse(p, out _, out _));
        Assert.False(E131.TryParse(new byte[40], out _, out _));
    }

    [Fact]
    public void MulticastGroupFollowsUniverse() =>
        Assert.Equal("239.255.1.2", E131.MulticastGroup(258).ToString());
}

public class DecoderTests
{
    static readonly ChannelMap Map = new();

    static byte[] Frame(params (int ch, byte v)[] set)
    {
        var f = new byte[512];
        foreach (var (ch, v) in set) f[ch - 1] = v;
        return f;
    }

    [Fact]
    public void HeldPulseCountsOnce()
    {
        var d = new YargDecoder(Map, Stopwatch.StartNew());
        d.OnFrame(Frame((Map.Keyframe, 3)));
        d.OnFrame(Frame((Map.Keyframe, 3)));             // YARG holds it ~60 ms: same event
        d.OnFrame(Frame());
        d.OnFrame(Frame((Map.Keyframe, 3)));
        Assert.Equal(2, d.Snapshot().Count(Trigger.Keyframe));
    }

    [Fact]
    public void FogOnSharedChannelIsNotABeat()
    {
        var d = new YargDecoder(Map, Stopwatch.StartNew());
        d.OnFrame(Frame((Map.Beatline, 255)));          // YARG's fog channel 2 = beatline 14 by default
        Assert.Equal(0, d.Snapshot().Count(Trigger.Beat));
        d.OnFrame(Frame((Map.Beatline, 1)));
        var s = d.Snapshot();
        Assert.Equal(1, s.Count(Trigger.Beat));
        Assert.Equal(1, s.Count(Trigger.Downbeat));
    }

    [Fact]
    public void SnareFiresOnItsBitOnly()
    {
        var d = new YargDecoder(Map, Stopwatch.StartNew());
        d.OnFrame(Frame((Map.Drums, (byte)Drum.Kick)));
        d.OnFrame(Frame((Map.Drums, (byte)(Drum.Kick | Drum.Red))));   // snare joins a held kick
        var s = d.Snapshot();
        Assert.Equal(1, s.Count(Trigger.Kick));
        Assert.Equal(1, s.Count(Trigger.Snare));
    }

    [Fact]
    public void ReadsCueStrobeAndLeds()
    {
        var d = new YargDecoder(Map, Stopwatch.StartNew());
        d.OnFrame(Frame((Map.Cue, (byte)Cue.Frenzy), (Map.Strobe, 191), (Map.Blue[3], 255)));
        var s = d.Snapshot();
        Assert.Equal(Cue.Frenzy, s.Cue);
        Assert.Equal(Strobe.Fast, s.Strobe);
        Assert.True(s.Leds[2, 3]);
        Assert.False(s.Leds[2, 4]);
    }

    [Fact]
    public void DefaultChannelsReportTheirCollisions() =>
        Assert.Contains(Map.Collisions(ChannelMap.YargFog, ChannelMap.YargStrobe), c => c.Contains("drums"));
}

public class ShowTests
{
    static Rig TestRig(int fixtures = 8)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rig-{Guid.NewGuid():N}.json");
        var shipped = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "rig.json"));
        File.WriteAllText(path, shipped);
        try
        {
            var rig = Rig.Load(path);
            rig.Fixtures = rig.Fixtures.Take(fixtures).ToList();
            return rig;
        }
        finally { File.Delete(path); }
    }

    static YargState State(Cue cue)
    {
        var s = new YargState { Cue = cue };
        return s;
    }

    [Fact]
    public void ShippedRigLoads()
    {
        var rig = TestRig();
        Assert.Equal(8, rig.Fixtures.Count);
        Assert.Equal("police", rig.Cues["Searchlights"]);
    }

    [Fact]
    public void UnknownKeyIsAnError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rig-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "fixtures": [ { "name": "a", "adress": 1 } ] }""");
        try { Assert.ThrowsAny<Exception>(() => Rig.Load(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PulseAlternatesColoursAndPeaksOnTheBeat()
    {
        var rig = TestRig();
        var show = new Show(rig);
        var s = State(Cue.Intro);
        s.LastAt[(int)Trigger.Beat] = s.LastAt[(int)Trigger.Downbeat] = 10.0;
        var hit = show.Render(s, 10.0);
        var later = show.Render(s, 12.0);
        Assert.Equal(rig.Colour("green"), hit[0]);
        Assert.Equal(rig.Colour("purple"), hit[1]);
        Assert.Equal(rig.Colour("green").Scale(rig.Looks["pulse-green-purple"].Floor).G, later[0].G, 2);
    }

    [Fact]
    public void PoliceSidesTakeTurns()
    {
        var show = new Show(TestRig());
        var s = State(Cue.Searchlights);
        s.BeatPeriod = 0.5;                       // 2 flashes per beat -> 4 flashes / s
        var a = show.Render(s, 0.1);              // flash 0: left half
        var b = show.Render(s, 0.6);              // flash 2: right half
        Assert.True(a.Take(4).All(c => c.R == 1) && a.Skip(4).All(c => c == Rgb.Black));
        Assert.True(b.Take(4).All(c => c == Rgb.Black) && b.Skip(4).All(c => c.B == 1));
    }

    [Fact]
    public void YargStrobeOverridesTheLook()
    {
        var show = new Show(TestRig());
        var s = State(Cue.Intro);
        s.Strobe = Strobe.Fast;                   // 10 Hz, on for the first 35% of each period
        Assert.All(show.Render(s, 0.01), c => Assert.Equal(Rgb.White, c));
        Assert.All(show.Render(s, 0.06), c => Assert.Equal(Rgb.Black, c));
    }

    [Fact]
    public void StageKitFoldsEightPositionsOntoFour()
    {
        var rig = TestRig(4);
        var s = State(Cue.Verse);
        s.Leds[0, 5] = true;                      // red at position 5 -> fixture 1 (5 % 4)
        s.Leds[2, 1] = true;                      // blue at position 1 -> fixture 1 too: magenta
        var o = new Show(rig).Render(s, 0);
        Assert.Equal(new Rgb(1, 0, 1), o[1]);
        Assert.Equal(Rgb.Black, o[0]);
    }

    [Fact]
    public void ChaosLightsAboutItsDensity()
    {
        var show = new Show(TestRig());
        var s = State(Cue.Frenzy);
        int lit = 0, total = 0;
        for (int beat = 0; beat < 400; beat++)
        {
            s.Counts[(int)Trigger.Beat] = beat;
            s.LastAt[(int)Trigger.Beat] = beat;
            lit += show.Render(s, beat).Count(c => c != Rgb.Black);
            total += 8;
        }
        Assert.InRange(lit / (double)total, 0.55, 0.65);
    }

    [Fact]
    public void UniverseRoundTripsThroughTheProfile()
    {
        var rig = TestRig(2);
        var colours = new[] { new Rgb(0.5, 0, 0.25), Rgb.White };
        var u = Universe.Build(rig, colours);
        Assert.Equal(128, u[0]);                  // PAR 1 dimmer carries the level
        Assert.Equal(255, u[1]);                  // ... and red goes out at full
        var back = Universe.Read(rig, u);
        Assert.Equal(0.5, back[0].R, 2);
        Assert.Equal(0.25, back[0].B, 2);
        Assert.Equal(Rgb.White, back[1]);
    }
}
