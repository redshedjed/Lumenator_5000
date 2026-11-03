using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using YargDmx;
using YargDmx.Engine;
using YargDmx.Input;

namespace Lumenator.Tests;

public class ReaperTests
{
    static string ChartJson(string events, bool repeat = false, double loopStart = 0, double loopEnd = 0, string project = "song.RPP") =>
        $$"""
        {"project":"{{project}}","repeat":{{(repeat ? 1 : 0)}},"loop":[{{loopStart}},{{loopEnd}}],"tempo":[[0,120],[10,60]],"events":[{{events}}]}
        """;

    static string Text(double t, string text, string track = "VENUE") => $$"""{"track":"{{track}}","t":{{t}},"text":"{{text}}"}""";
    static string Note(double t, int note, string track = "VENUE", double? end = null) =>
        $$"""{"track":"{{track}}","t":{{t}},"end":{{end ?? t + 0.05}},"note":{{note}}}""";

    /// <summary>A player and the decoder it drives, with the transport under the test's control.</summary>
    sealed class Rehearsal
    {
        public readonly YargDecoder Decoder = new(new ChannelMap(), Stopwatch.StartNew());
        public readonly ReaperPlayer Player;
        public double Now = 100;
        public YargState State => Decoder.Snapshot();

        public Rehearsal(string chart)
        {
            Player = new ReaperPlayer(Decoder);
            Player.SetChart(ReaperChart.Parse(chart));
            Player.SetChartAlive(true);
            Player.Tick(Now);
        }

        public void Send(params (string, float)[] messages) { Player.OnOsc(messages, Now); Player.Tick(Now); }
        public void Play() => Send(("/stop", 0), ("/pause", 0), ("/play", 1));
        public void Pause() => Send(("/stop", 1), ("/pause", 1), ("/play", 0));
        public void Stop() => Send(("/stop", 1), ("/play", 0));

        /// <summary>Song time reaches <paramref name="pos"/> at REAPER's pace, reporting as it goes.</summary>
        public void PlayTo(double from, double pos)
        {
            for (double t = from; t < pos; t += 0.1)
            {
                Now += 0.1;
                Send(("/time", (float)Math.Min(t + 0.1, pos)));
            }
        }

        public void Jump(float to) { Now += 0.1; Send(("/time", to)); }
    }

    [Fact]
    public void ReadsCuesEffectsFogStrobesBeatsAndKeyframes()
    {
        var chart = ReaperChart.Parse(ChartJson(string.Join(",",
            Text(1, "[lighting (loop_warm)]"), Text(2, "[lighting (strobe_fast)]"), Text(3, "[FogOn]"), Text(4, "[film_b+w.pp]"),
            Text(5, "[lighting ()]"), Text(6, "[coop_all_behind]"), Note(7, 48), Note(8, 49), Note(9, 50),
            Note(10, 12, "BEAT"), Note(11, 13, "BEAT"))));
        Assert.Equal(new ChartEvent[]
        {
            new(1, ChartKind.Cue, (int)Cue.WarmLoop), new(2, ChartKind.Strobe, (int)Strobe.Fast), new(3, ChartKind.Fog, 1),
            new(4, ChartKind.Effect, (int)PostProcessing.BlackAndWhite), new(5, ChartKind.Cue, (int)Cue.Default),
            new(7, ChartKind.Keyframe, 1), new(8, ChartKind.Keyframe, -1), new(9, ChartKind.Keyframe, 0),
            new(10, ChartKind.Beat, 1), new(11, ChartKind.Beat, 0),
        }, chart.Events);
        Assert.Equal(new[] { "[coop_all_behind]" }, chart.Ignored);
        Assert.Equal(120, chart.BpmAt(5));
        Assert.Equal(60, chart.BpmAt(10));
    }

    [Fact]
    public void ACueOnTheSameTickComesBeforeItsKeyframeAndStrobe()
    {
        var chart = ReaperChart.Parse(ChartJson(string.Join(",", Note(1, 48), Text(1, "[lighting (strobe_slow)]"), Text(1, "[lighting (stomp)]"))));
        Assert.Equal(new[] { ChartKind.Cue, ChartKind.Strobe, ChartKind.Keyframe }, chart.Events.Select(e => e.Kind));
    }

    [Fact]
    public void DrumPadsAreCymbalsUnlessATomMarkerCoversThem()
    {
        var chart = ReaperChart.Parse(ChartJson(string.Join(",",
            Note(1, 96, "PART DRUMS"), Note(2, 97, "PART DRUMS"), Note(3, 98, "PART DRUMS"),
            Note(3.9, 110, "PART DRUMS", end: 4.5), Note(4, 98, "PART DRUMS"), Note(5, 100, "PART GUITAR"))));
        Assert.Equal(new (Instrument, byte)[]
        {
            (Instrument.Drums, (byte)Drum.Kick), (Instrument.Drums, (byte)Drum.Red), (Instrument.Drums, (byte)Drum.YellowCymbal),
            (Instrument.Drums, (byte)Drum.YellowPad), (Instrument.Guitar, 32),
        }, chart.Notes.Select(n => (n.Instrument, n.Bits)));
    }

    [Fact]
    public void WaitsForTheFirstCueIgnoringKeyframesBeforeIt()
    {
        var r = new Rehearsal(ChartJson(string.Join(",", Note(1, 48), Text(2, "[lighting (stomp)]"), Note(3, 48))));
        r.Play();
        r.PlayTo(0, 1.5);
        Assert.True(r.State.Waiting);
        Assert.Equal(0, r.State.Count(Trigger.Keyframe));

        r.PlayTo(1.5, 2.5);
        Assert.False(r.State.Waiting);
        Assert.Equal(Cue.Stomp, r.State.Cue);
        r.PlayTo(2.5, 3.5);
        Assert.Equal(1, r.State.Count(Trigger.Keyframe));
    }

    [Fact]
    public void AScrubGoesDarkWithoutReplayingAndTheNextCueWakesIt()
    {
        var r = new Rehearsal(ChartJson(string.Join(",", Text(1, "[lighting (frenzy)]"), Text(1, "[FogOn]"),
            Text(3, "[lighting (stomp)]"), Note(20, 48), Text(30, "[lighting (verse)]"))));
        r.Play();
        r.PlayTo(0, 1.5);
        Assert.Equal(Cue.Frenzy, r.State.Cue);
        Assert.True(r.State.Fog);

        r.Jump(19);                                         // past the stomp, which isn't replayed
        Assert.True(r.State.Waiting);
        Assert.Equal(Cue.NoCue, r.State.Cue);
        Assert.False(r.State.Fog);
        r.PlayTo(19, 21);                                   // a keyframe with no cue behind it
        Assert.Equal(0, r.State.Count(Trigger.Keyframe));

        r.Jump(29.5f);
        r.PlayTo(29.5, 30.5);
        Assert.Equal(Cue.Verse, r.State.Cue);
        Assert.False(r.State.Waiting);
    }

    [Fact]
    public void WaitsForREAPERToMoveAfterPlayThenCatchesACueOnTheCursor()
    {
        var r = new Rehearsal(ChartJson(Text(5, "[lighting (frenzy)]")));
        r.Jump(5);
        r.Play();
        r.Now += 0.3;                                       // REAPER's startup: position hasn't moved
        r.Send(("/time", 5f));
        Assert.True(r.State.Waiting);
        r.Now += 0.1;
        r.Send(("/time", 5.1f));
        Assert.Equal(Cue.Frenzy, r.State.Cue);
    }

    [Fact]
    public void RunsItsOwnClockBetweenREAPERsReports()
    {
        var r = new Rehearsal(ChartJson(Text(1.05, "[lighting (frenzy)]")));
        r.Play();
        r.PlayTo(0, 1);
        Assert.True(r.State.Waiting);
        r.Now += 0.06;                                      // no report yet, but the song has moved on
        r.Player.Tick(r.Now);
        Assert.Equal(Cue.Frenzy, r.State.Cue);
    }

    [Fact]
    public void StopGoesDarkAndPauseHoldsWithoutRepeatingEvents()
    {
        var r = new Rehearsal(ChartJson(string.Join(",", Text(1, "[lighting (frenzy)]"), Note(1.5, 12, "BEAT"), Note(4, 12, "BEAT"))));
        r.Play();
        r.PlayTo(0, 2);
        Assert.Equal(1, r.State.Count(Trigger.Beat));

        r.Pause();
        Assert.True(r.State.Frozen);
        Assert.True(r.State.Paused);
        Assert.False(r.State.Waiting);
        Assert.Equal(Cue.Frenzy, r.State.Cue);

        r.Now += 5;
        r.Play();
        Assert.False(r.State.Frozen);
        r.PlayTo(2, 3);
        Assert.Equal(1, r.State.Count(Trigger.Beat));       // the song carried on from where it was
        Assert.Equal(Cue.Frenzy, r.State.Cue);

        r.Stop();
        Assert.True(r.State.Waiting);
        Assert.Equal(Cue.NoCue, r.State.Cue);
    }

    [Fact]
    public void ALoopGoingRoundKeepsTheLookAndPlaysItsEventsAgain()
    {
        var r = new Rehearsal(ChartJson(string.Join(",", Text(1, "[lighting (frenzy)]"), Note(5, 12, "BEAT"), Note(5.95, 12, "BEAT")),
            repeat: true, loopStart: 4, loopEnd: 6));
        r.Play();
        r.PlayTo(0, 5.9);
        Assert.Equal(1, r.State.Count(Trigger.Beat));

        r.Now += 0.1;
        r.Send(("/time", 4.05f));                           // round again, as REAPER reports it
        Assert.False(r.State.Waiting);
        Assert.Equal(Cue.Frenzy, r.State.Cue);
        Assert.Equal(2, r.State.Count(Trigger.Beat));       // the end of the loop still played

        r.PlayTo(4.05, 5.5);
        Assert.Equal(3, r.State.Count(Trigger.Beat));
    }

    [Fact]
    public void AJumpBackThatIsntTheLoopIsAScrub()
    {
        var r = new Rehearsal(ChartJson(Text(1, "[lighting (frenzy)]"), repeat: true, loopStart: 4, loopEnd: 20));
        r.Play();
        r.PlayTo(0, 10);
        r.Jump(4);                                          // to the loop's start, but nowhere near its end
        Assert.True(r.State.Waiting);
    }

    [Fact]
    public void ANewCueEndsAStrobeAsInYarg()
    {
        var r = new Rehearsal(ChartJson(string.Join(",", Text(1, "[lighting (frenzy)]"), Text(2, "[lighting (strobe_fast)]"), Text(3, "[lighting (stomp)]"))));
        r.Play();
        r.PlayTo(0, 2.5);
        Assert.Equal(Strobe.Fast, r.State.Strobe);
        r.PlayTo(2.5, 3.5);
        Assert.Equal(Strobe.Off, r.State.Strobe);
    }

    [Fact]
    public void AnEditPlaysOnButAnotherProjectStartsOver()
    {
        var r = new Rehearsal(ChartJson(Text(1, "[lighting (frenzy)]")));
        r.Play();
        r.PlayTo(0, 2);
        r.Player.SetChart(ReaperChart.Parse(ChartJson(Text(1, "[lighting (frenzy)]") + "," + Text(3, "[lighting (stomp)]"))));
        r.PlayTo(2, 2.5);
        Assert.Equal(Cue.Frenzy, r.State.Cue);              // the edit didn't reset anything
        r.PlayTo(2.5, 3.5);
        Assert.Equal(Cue.Stomp, r.State.Cue);               // and its new cue plays

        r.Player.SetChart(ReaperChart.Parse(ChartJson("", project: "other.RPP")));
        r.Player.Tick(r.Now);
        Assert.True(r.State.Waiting);
    }

    [Fact]
    public void TheSameNoteStruckAgainCountsAsAHit()
    {
        var r = new Rehearsal(ChartJson(string.Join(",", Note(1, 97, "PART DRUMS", end: 1.2), Note(1.2, 97, "PART DRUMS", end: 1.4))));
        r.Play();
        r.PlayTo(0, 2);
        Assert.Equal(2, r.State.Count(Trigger.Snare));
    }

    [Fact]
    public void SaysWhenTheExportScriptIsntRunning()
    {
        var r = new Rehearsal(ChartJson(""));
        r.Play();
        Assert.Equal("REAPER", r.State.Source);
        r.Player.SetChartAlive(false);
        r.Player.Tick(r.Now);
        Assert.Contains("lumenator_export.lua", r.State.Source);
    }

    static Rig TwoLights() => Rig.Parse("""
        {
          "profiles": { "p": {} },
          "fixtures": [ { "name": "a", "address": 1, "profile": "p" }, { "name": "b", "address": 4, "profile": "p" } ],
          "looks": {
            "pulse": { "type": "pulse", "colors": ["#ffffff"], "trigger": "rate", "rate_hz": 1, "decay": 0.2, "floor": 0, "fade": 5 },
            "trails": { "type": "solid", "colors": ["#ffffff"], "fade": 0 }
          },
          "cues": { "Stomp": "trails", "*": "pulse" },
          "post_processing": { "Trails": { "trails": 5 } }
        }
        """);

    [Fact]
    public void WaitingIsDarkEvenWhereNoCueHasALookAndTheNextCueKicksOnWithoutAFade()
    {
        var show = new Show(TwoLights());
        var s = new YargState { Cue = Cue.Stomp };
        Assert.Equal(1, show.Render(s, 1).Max(c => c.Level));

        s.Waiting = true;
        s.Cue = Cue.NoCue;                                  // NoCue has a look, but waiting beats it
        Assert.Equal(0, show.Render(s, 2).Max(c => c.Level));

        s.Waiting = false;
        s.Cue = Cue.Stomp;
        Assert.Equal(1, show.Render(s, 2.01).Max(c => c.Level));
    }

    [Fact]
    public void AStrobeFlashesWhileWaiting()
    {
        var show = new Show(TwoLights());
        var s = new YargState { Waiting = true, Strobe = Strobe.Fast, BeatPeriod = 0.5 };
        var levels = Enumerable.Range(0, 40).Select(i => show.Render(s, 10 + i * 0.02).Max(c => c.Level)).ToArray();
        Assert.Contains(1.0, levels);
        Assert.Contains(0.0, levels);
    }

    [Fact]
    public void FrozenHoldsTheLightsThenLetsGo()
    {
        var show = new Show(TwoLights());
        var s = new YargState { Cue = Cue.Frenzy };
        show.Render(s, 10.0);
        s.Frozen = true;
        var held = show.Render(s, 10.05);
        Assert.True(held[0].Level > 0);
        Assert.Equal(held, show.Render(s, 10.5));
        Assert.Equal(held, show.Render(s, 20.05));
        s.Frozen = false;
        Assert.NotEqual(held, show.Render(s, 20.5));
    }

    static byte[] Message(string address, float value)
    {
        byte[] Pad(string s)
        {
            var b = Encoding.ASCII.GetBytes(s + "\0");
            Array.Resize(ref b, (b.Length + 3) / 4 * 4);
            return b;
        }
        var f = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(f, value);
        return Pad(address).Concat(Pad(",f")).Concat(f).ToArray();
    }

    static byte[] Bundle(params byte[][] messages)
    {
        var o = new List<byte>(Encoding.ASCII.GetBytes("#bundle\0"));
        o.AddRange(new byte[8]);
        foreach (var m in messages)
        {
            var size = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(size, m.Length);
            o.AddRange(size);
            o.AddRange(m);
        }
        return o.ToArray();
    }

    [Fact]
    public void ReadsOscBundlesAndSurvivesRubbish()
    {
        var found = Osc.Read(Bundle(Message("/play", 1), Message("/time", 1.5f)));
        Assert.Equal(new[] { ("/play", 1f), ("/time", 1.5f) }, found);
        Assert.Equal(("/time", 2f), Osc.Read(Message("/time", 2)).Single());
        Assert.Empty(Osc.Read(new byte[] { 1, 2, 3 }));
        Assert.Empty(Osc.Read(Encoding.ASCII.GetBytes("#bundle\0").Concat(new byte[8]).Concat(new byte[] { 0, 0, 1, 0 }).ToArray()));
        Assert.Empty(Osc.Read(Encoding.ASCII.GetBytes("/time\0\0\0,s\0\0abc\0")));   // not a float
    }

    [Fact]
    public async Task FollowsRealOscPacketsAndTheChartFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chart-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, ChartJson(Text(1, "[lighting (frenzy)]")));
        var clock = Stopwatch.StartNew();
        var decoder = new YargDecoder(new ChannelMap(), clock);
        try
        {
            using var input = new ReaperInput(decoder, clock, port: 0, chartPath: path);
            using var udp = new UdpClient();
            var to = new IPEndPoint(IPAddress.Loopback, input.Port);
            async Task<bool> Until(Func<YargState, bool> ok)
            {
                for (int i = 0; i < 300 && !ok(decoder.Snapshot()); i++)
                    await Task.Delay(10);
                return ok(decoder.Snapshot());
            }

            Assert.True(await Until(s => s.Source == "REAPER: stopped"), decoder.Snapshot().Source);
            var play = Bundle(Message("/stop", 0), Message("/pause", 0), Message("/play", 1), Message("/time", 0.5f));
            await udp.SendAsync(play, play.Length, to);
            var move = Message("/time", 0.6f);
            await udp.SendAsync(move, move.Length, to);
            Assert.True(await Until(s => s.Source == "REAPER"), decoder.Snapshot().Source);
            Assert.True(await Until(s => s.Cue == Cue.Frenzy), "the clock should reach the cue at 1 s");

            // the file changing is picked up, without a reset
            File.WriteAllText(path, ChartJson(Text(1, "[lighting (frenzy)]") + "," + Text(2, "[lighting (stomp)]")));
            Assert.True(await Until(s => s.Cue == Cue.Stomp), "an edit should play on");
        }
        finally { File.Delete(path); }
    }
}
