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
    public void ReadsTheSender()
    {
        var cid = Guid.NewGuid();
        var p = E131.Build(1, new byte[512], 0, cid, "YARG");
        Assert.Equal((cid, "YARG"), E131.Sender(p));
    }

    [Fact]
    public void FollowsOneSenderUntilItGoesQuiet()
    {
        Guid yarg = Guid.NewGuid(), sim = Guid.NewGuid();
        var lockOn = new SourceLock();
        Assert.True(lockOn.Follows(yarg, "YARG", 0));
        Assert.False(lockOn.Follows(sim, "YARG simulator", 20));         // never interleaved
        Assert.True(lockOn.Follows(yarg, "YARG", 1000));
        Assert.Equal(new[] { "YARG simulator" }, lockOn.Ignored(1000));
        Assert.True(lockOn.Follows(sim, "YARG simulator", 1000 + SourceLock.TimeoutMs));   // YARG went quiet
        Assert.Equal("YARG simulator", lockOn.Source);

        var only = new SourceLock { Only = sim };
        Assert.False(only.Follows(yarg, "YARG", 0));
        Assert.False(only.Follows(yarg, "YARG", 10_000));                // --sim: never YARG
        Assert.True(only.Follows(sim, "YARG simulator", 10_000));
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
    public void KeyframesWalkNextPreviousFirst()
    {
        var d = new YargDecoder(Map, Stopwatch.StartNew());
        foreach (byte k in new byte[] { 3, 0, 3, 0, 13 })
            d.OnFrame(Frame((Map.Keyframe, k)));
        var s = d.Snapshot();
        Assert.Equal((1, 2), (s.KeyframeStep, s.KeyframePrev));
        d.OnFrame(Frame());
        d.OnFrame(Frame((Map.Keyframe, 23)));
        Assert.Equal(0, d.Snapshot().KeyframeStep);
    }

    [Fact]
    public void ReadsHeldNotesTomsAndEffect()
    {
        var d = new YargDecoder(Map, Stopwatch.StartNew());
        d.OnFrame(Frame((Map.Guitar, 2 | 8), (Map.Drums, (byte)Drum.BluePad), (Map.PostProcessing, 74)));
        var s = d.Snapshot();
        Assert.Equal(10, s.Held[(int)Instrument.Guitar]);
        Assert.False(double.IsNegativeInfinity(s.HeldAt[(int)Instrument.Guitar, 3]));
        Assert.True(double.IsNegativeInfinity(s.HeldAt[(int)Instrument.Guitar, 2]));
        Assert.Equal(1, s.Count(Trigger.Tom));
        Assert.Equal(1, s.Count(Trigger.Drums));
        Assert.Equal(PostProcessing.SepiaTone, s.PostProcessing);
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
        Assert.Equal("searchlights", rig.Cues["Searchlights"]);
        Assert.Equal("white", rig.PostProcessing["BlackAndWhite"].Tint);          // a plain colour = a full tint
        Assert.Equal(new[] { "red", "blue" }, rig.PostProcessing["PolarizedRedAndBlue"].Polarize);
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
        var s = State(Cue.Intro);                 // blue / purple on the bar line
        s.LastAt[(int)Trigger.Beat] = s.LastAt[(int)Trigger.Downbeat] = 10.0;
        var hit = show.Render(s, 10.0);
        var later = show.Render(s, 20.0);
        Assert.Equal(rig.Colour("blue"), hit[0]);
        Assert.Equal(rig.Colour("purple"), hit[1]);
        Assert.Equal(rig.Looks["intro"].Floor, later[0].B, 2);
    }

    [Fact]
    public void CueChangesCrossfadeOverTheNewLooksFade()
    {
        var show = new Show(TestRig());
        var s = State(Cue.BlackoutSpotlight);    // a steady underlight
        show.Render(s, 10.0);
        s.Cue = Cue.BlackoutSlow;                 // fades to black over 2 s
        double Level(double t) => show.Render(s, t).Max(c => c.Level);
        double start = Level(10.0), mid = Level(11.0), end = Level(12.5);
        Assert.True(start > 0.05 && mid > 0 && mid < start, $"{start} {mid}");
        Assert.Equal(0, end);
    }

    [Fact]
    public void FlareIsOneHitAsTheCueStarts()
    {
        var rig = TestRig();
        var s = State(Cue.FlareFast);
        s.LastAt[(int)Trigger.Cue] = 10.0;
        s.LastAt[(int)Trigger.Beat] = 11.0;       // later beats don't flash it again
        var show = new Show(rig);
        Assert.Equal(Rgb.White, show.Render(s, 10.0)[0]);
        var after = show.Render(s, 12.0)[0];
        Assert.Equal(rig.Colour("amber").Scale(rig.Looks["flare-fast"].Floor).R, after.R, 2);
    }

    [Fact]
    public void RedFilterTurnsAFlareIntoAFadingRedPulse()
    {
        var show = new Show(TestRig());
        var s = State(Cue.FlareSlow);
        s.PostProcessing = PostProcessing.PhotoNegativeRedAndBlack;
        s.LastAt[(int)Trigger.Cue] = 10.0;
        var hit = show.Render(s, 10.0)[0];
        Assert.Equal(1, hit.R, 2);
        Assert.Equal(0, hit.G + hit.B, 2);                            // the white flare comes out red
        s.Cue = Cue.BlackoutSlow;                                     // a 16th later: fade out over 2 s
        show.Render(s, 10.1);
        var mid = show.Render(s, 11.1)[0];
        Assert.True(mid.R > 0.02 && mid.R < 0.5, $"{mid}");
        Assert.Equal(0, show.Render(s, 12.2)[0].R, 3);
    }

    [Fact]
    public void StrobeFollowsTheTempo()
    {
        var show = new Show(TestRig());
        var s = State(Cue.Chorus);
        s.Strobe = Strobe.Slow;                   // 2 flashes per beat
        s.BeatPeriod = 0.5;
        s.Counts[(int)Trigger.Beat] = 10;
        s.LastAt[(int)Trigger.Beat] = 5.0;
        Assert.Equal(Rgb.White, show.Render(s, 5.05)[0]);    // 0.1 of a beat: on
        Assert.Equal(Rgb.Black, show.Render(s, 5.15)[0]);    // 0.6 of the way through the first 8th: off
        Assert.Equal(Rgb.White, show.Render(s, 5.26)[0]);    // second 8th of the beat
    }

    [Fact]
    public void StompToggleFlipsOnEachKeyframe()
    {
        var rig = TestRig();
        rig.Cues["Stomp"] = "stomp-toggle";
        var show = new Show(rig);
        var s = State(Cue.Stomp);
        Assert.Equal(Rgb.White, show.Render(s, 1.0)[0]);     // on as the cue starts
        s.Counts[(int)Trigger.Keyframe] = 1;
        s.LastAt[(int)Trigger.Keyframe] = 2.0;
        Assert.Equal(Rgb.Black, show.Render(s, 2.5)[0]);
        s.Counts[(int)Trigger.Keyframe] = 2;
        s.LastAt[(int)Trigger.Keyframe] = 3.0;
        Assert.Equal(Rgb.White, show.Render(s, 3.5)[0]);
    }

    [Fact]
    public void ChaseMovesWithTheBeat()
    {
        var show = new Show(TestRig());
        var s = State(Cue.Sweep);                 // white and blue spots, a lap every 4 beats
        s.BeatPeriod = 0.5;
        s.Counts[(int)Trigger.Beat] = 0;
        s.LastAt[(int)Trigger.Beat] = 0;
        int Brightest(double t) => show.Render(s, t).Select((c, i) => (c.R, i)).MaxBy(x => x.R).i;
        Assert.Equal(0, Brightest(0.125));       // a quarter beat in: the white spot over the first light
        s.Counts[(int)Trigger.Beat] = 1;          // a beat later: a quarter lap on, two lights over
        s.LastAt[(int)Trigger.Beat] = 0.5;
        Assert.Equal(2, Brightest(0.625));
    }

    [Fact]
    public void LayersCombineLooks()
    {
        var rig = TestRig();
        var show = new Show(rig);
        var s = State(Cue.Dischord);              // keyframe colours plus a red snare hit
        s.LastAt[(int)Trigger.Snare] = 10.0;
        Assert.All(show.Render(s, 10.0), c => Assert.Equal(1, c.R));
    }

    [Fact]
    public void EffectsMirrorInvertPolarizeAndLinger()
    {
        var rig = TestRig();
        var s = State(Cue.SilhouettesSpotlight);
        s.HeldAt[(int)Instrument.Guitar, 1] = 10.0;   // green, far left
        s.PostProcessing = PostProcessing.Mirror;
        var m = new Show(rig).Render(s, 10.0);
        Assert.True(m[7].G > 0.5 && m[7].R > 0);      // the right end copies the left, warmed
        s.PostProcessing = PostProcessing.PhotoNegative;
        Assert.Equal(new Rgb(1, 0, 1), new Show(rig).Render(s, 10.0)[0]);   // green -> magenta
        s.PostProcessing = PostProcessing.PolarizedRedAndBlue;
        var p = new Show(rig).Render(s, 10.0);
        Assert.Contains(p[0], new[] { rig.Colour("red"), rig.Colour("blue") });
        Assert.Equal(Rgb.Black, p[7]);                 // dim lights go dark
        s.PostProcessing = PostProcessing.TrailsLong;
        var show = new Show(rig);
        show.Render(s, 10.0);
        Assert.True(show.Render(s, 10.5)[0].G > 0.3); // still lit after the note's own fade
    }

    [Fact]
    public void EveryCueHasItsOwnLookAndNoCueFallsBackToTheBand()
    {
        var rig = TestRig();
        Assert.All(Enum.GetValues<Cue>(), c => Assert.True(rig.Cues.ContainsKey(c.ToString()), c.ToString()));
        Assert.DoesNotContain("stagekit", rig.Cues.Values);
        Assert.Equal("band", rig.Cues["NoCue"]);
    }

    [Fact]
    public void KeyframeStepCrossfadesAndCanStepBack()
    {
        var rig = TestRig();
        var show = new Show(rig);
        var s = State(Cue.CoolManual);            // blue, cyan, purple; the whole rig one colour
        s.KeyframePrev = 1;
        s.KeyframeStep = 0;                       // previous: cyan back to blue
        s.LastAt[(int)Trigger.Keyframe] = 10.0;
        Assert.Equal(rig.Colour("cyan"), show.Render(s, 10.0)[0]);
        var mid = show.Render(s, 10.2)[0];
        Assert.True(mid.G > 0 && mid.G < rig.Colour("cyan").G);
        Assert.Equal(rig.Colour("blue"), show.Render(s, 11.0)[0]);
    }

    [Fact]
    public void FretsLightTheirSectionAndFade()
    {
        var rig = TestRig();
        var show = new Show(rig);
        var s = State(Cue.SilhouettesSpotlight);  // guitar: green red yellow blue orange, left to right
        s.HeldAt[(int)Instrument.Guitar, 1] = 10.0;   // green, let go at 10 s
        var held = show.Render(s, 10.0);
        Assert.Equal(rig.Colour("green"), held[0]);
        Assert.Equal(rig.Colour("orange").Scale(rig.Looks["spotlight"].Floor), held[7]);
        Assert.True(show.Render(s, 10.3)[0].G < 0.5);
        s.HeldAt[(int)Instrument.Guitar, 0] = 11.0;   // open note: the whole rig
        Assert.All(show.Render(s, 11.0), c => Assert.True(c.Level > 0.9));
    }

    [Fact]
    public void ScreenEffectTintsTheRig()
    {
        var rig = TestRig();
        var s = State(Cue.Chorus);
        s.LastAt[(int)Trigger.Beat] = s.LastAt[(int)Trigger.Downbeat] = 10.0;
        s.PostProcessing = PostProcessing.BlackAndWhite;
        Assert.All(new Show(rig).Render(s, 10.0), c => Assert.Equal(Rgb.White, c));
    }

    [Fact]
    public void PoliceSidesTakeTurns()
    {
        var rig = TestRig();
        rig.Cues["Searchlights"] = "police";
        var show = new Show(rig);
        var s = State(Cue.Searchlights);
        s.BeatPeriod = 0.5;                       // 2 flashes per beat -> 4 flashes / s
        var a = show.Render(s, 0.1);              // flash 0: left half
        var b = show.Render(s, 0.6);              // flash 2: right half
        Assert.True(a.Take(4).All(c => c.R == 1) && a.Skip(4).All(c => c == Rgb.Black));
        Assert.True(b.Take(4).All(c => c == Rgb.Black) && b.Skip(4).All(c => c.B == 1));
    }

    [Fact]
    public void MenuWaveRollsSmoothlyAcrossTheRig()
    {
        var show = new Show(TestRig());
        var s = State(Cue.Menu);
        var a = show.Render(s, 10.0);
        var b = show.Render(s, 10.025);           // one frame later
        Assert.Equal("menu-wave", show.LookName);
        Assert.All(a.Zip(b), p => Assert.True(Math.Abs(p.First.R - p.Second.R) + Math.Abs(p.First.G - p.Second.G)
                                              + Math.Abs(p.First.B - p.Second.B) < 0.02));
        Assert.NotEqual(a[0], a[^1]);             // the ends of the rig are at different points of the wave
    }

    [Fact]
    public void YargStrobeOverridesTheLook()
    {
        var show = new Show(TestRig());
        var s = State(Cue.Chorus);
        s.Strobe = Strobe.Fast;                   // 10 Hz, on for the first 35% of each period
        Assert.All(show.Render(s, 0.01), c => Assert.Equal(Rgb.White, c));
        Assert.All(show.Render(s, 0.06), c => Assert.Equal(Rgb.Black, c));
    }

    [Fact]
    public void StageKitFoldsEightPositionsOntoFour()
    {
        var rig = TestRig(4);
        rig.Cues["Verse"] = "stagekit";
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
