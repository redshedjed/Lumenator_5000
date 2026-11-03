using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace YargDmx.Input;

/// <summary>One thing the chart says at a moment. Same-tick events run in this order, so a strobe on a
/// cue's tick outlasts the cue clearing it, and a keyframe on a cue's tick counts for that cue.</summary>
public enum ChartKind { Cue, Strobe, Fog, Effect, Beat, Keyframe }

/// <summary>Value: the Cue, Strobe or PostProcessing; Fog 1 on, 0 off; Beat 1 for a bar line; Keyframe 1
/// next, -1 previous, 0 first.</summary>
public readonly record struct ChartEvent(double At, ChartKind Kind, int Value);

/// <summary>A note on one instrument: the bits it holds on that instrument's channel.</summary>
public readonly record struct ChartNote(double At, double End, Instrument Instrument, byte Bits);

/// <summary>A REAPER project's chart as reaper/lumenator_export.lua writes it, times in seconds.</summary>
public sealed class ReaperChart
{
    public string Project { get; private init; } = "";
    public bool Repeat { get; private init; }
    public double LoopStart { get; private init; }
    public double LoopEnd { get; private init; }
    public IReadOnlyList<ChartEvent> Events { get; private init; } = Array.Empty<ChartEvent>();
    public IReadOnlyList<ChartNote> Notes { get; private init; } = Array.Empty<ChartNote>();
    public IReadOnlyList<(double At, double Bpm)> Tempo { get; private init; } = Array.Empty<(double, double)>();
    /// <summary>VENUE text this doesn't act on (camera cuts and the like), each once.</summary>
    public IReadOnlyList<string> Ignored { get; private init; } = Array.Empty<string>();

    static readonly Dictionary<string, Cue> Cues = new()
    {
        [""] = Cue.Default, ["intro"] = Cue.Intro, ["verse"] = Cue.Verse, ["chorus"] = Cue.Chorus,
        ["loop_warm"] = Cue.WarmLoop, ["loop_cool"] = Cue.CoolLoop, ["manual_warm"] = Cue.WarmManual,
        ["manual_cool"] = Cue.CoolManual, ["dischord"] = Cue.Dischord, ["stomp"] = Cue.Stomp,
        ["harmony"] = Cue.Harmony, ["frenzy"] = Cue.Frenzy, ["silhouettes"] = Cue.Silhouettes,
        ["silhouettes_spot"] = Cue.SilhouettesSpotlight, ["searchlights"] = Cue.Searchlights, ["sweep"] = Cue.Sweep,
        ["blackout_fast"] = Cue.BlackoutFast, ["blackout_slow"] = Cue.BlackoutSlow,
        ["blackout_spot"] = Cue.BlackoutSpotlight, ["flare_slow"] = Cue.FlareSlow, ["flare_fast"] = Cue.FlareFast,
        ["bre"] = Cue.BigRockEnding,
    };

    static readonly Dictionary<string, Strobe> Strobes = new()
    {
        ["strobe_off"] = Strobe.Off, ["strobe_slow"] = Strobe.Slow, ["strobe_medium"] = Strobe.Medium,
        ["strobe_fast"] = Strobe.Fast, ["strobe_fastest"] = Strobe.Fastest,
    };

    /// <summary>The chart events for YARG's screen effects (the README's table).</summary>
    static readonly Dictionary<string, PostProcessing> Effects = new()
    {
        ["[ProFilm_a.pp]"] = PostProcessing.Default, ["[bloom.pp]"] = PostProcessing.Bloom,
        ["[bright.pp]"] = PostProcessing.Bright, ["[film_contrast.pp]"] = PostProcessing.Contrast,
        ["[ProFilm_mirror_a.pp]"] = PostProcessing.Mirror, ["[photo_negative.pp]"] = PostProcessing.PhotoNegative,
        ["[posterize.pp]"] = PostProcessing.Posterize, ["[film_b+w.pp]"] = PostProcessing.BlackAndWhite,
        ["[film_sepia_ink.pp]"] = PostProcessing.SepiaTone, ["[film_silvertone.pp]"] = PostProcessing.SilverTone,
        ["[photocopy.pp]"] = PostProcessing.ChoppyBlackAndWhite,
        ["[horror_movie_special.pp]"] = PostProcessing.PhotoNegativeRedAndBlack,
        ["[contrast_a.pp]"] = PostProcessing.PolarizedBlackAndWhite,
        ["[ProFilm_psychedelic_blue_red.pp]"] = PostProcessing.PolarizedRedAndBlue,
        ["[ProFilm_b.pp]"] = PostProcessing.DesaturatedRed, ["[desat_blue.pp]"] = PostProcessing.DesaturatedBlue,
        ["[film_contrast_red.pp]"] = PostProcessing.ContrastRed, ["[film_contrast_green.pp]"] = PostProcessing.ContrastGreen,
        ["[film_contrast_blue.pp]"] = PostProcessing.ContrastBlue, ["[film_16mm.pp]"] = PostProcessing.GrainyFilm,
        ["[shitty_tv.pp]"] = PostProcessing.GrainyChromaticAbberation, ["[video_a.pp]"] = PostProcessing.Scanlines,
        ["[video_bw.pp]"] = PostProcessing.ScanlinesBlackAndWhite, ["[film_blue_filter.pp]"] = PostProcessing.ScanlinesBlue,
        ["[video_security.pp]"] = PostProcessing.ScanlinesSecurity, ["[clean_trails.pp]"] = PostProcessing.Trails,
        ["[video_trails.pp]"] = PostProcessing.TrailsLong, ["[desat_posterize_trails.pp]"] = PostProcessing.TrailsDesaturated,
        ["[flicker_trails.pp]"] = PostProcessing.TrailsFlickery, ["[space_woosh.pp]"] = PostProcessing.TrailsSpacey,
    };

    static readonly Dictionary<string, Instrument> Parts = new()
    {
        ["PART GUITAR"] = Instrument.Guitar, ["PART BASS"] = Instrument.Bass,
        ["PART KEYS"] = Instrument.Keys, ["PART DRUMS"] = Instrument.Drums,
    };

    public static ReaperChart Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var events = new List<ChartEvent>();
        var ignored = new HashSet<string>();
        var raw = new List<(double At, double End, Instrument Part, int Pitch)>();
        foreach (var e in root.GetProperty("events").EnumerateArray())
        {
            string track = e.GetProperty("track").GetString() ?? "";
            double at = e.GetProperty("t").GetDouble();
            if (e.TryGetProperty("text", out var text))
            {
                if (track == "VENUE")
                {
                    string t = (text.GetString() ?? "").Trim();
                    if (Venue(at, t) is { } ev) events.Add(ev);
                    else ignored.Add(t);
                }
                continue;
            }
            int pitch = e.GetProperty("note").GetInt32();
            if (track == "BEAT" && pitch is 12 or 13)
                events.Add(new(at, ChartKind.Beat, pitch == 12 ? 1 : 0));
            else if (track == "VENUE" && pitch is >= 48 and <= 50)
                events.Add(new(at, ChartKind.Keyframe, pitch switch { 48 => 1, 49 => -1, _ => 0 }));
            else if (Parts.TryGetValue(track, out var part))
                raw.Add((at, e.GetProperty("end").GetDouble(), part, pitch));
        }

        var tempo = root.GetProperty("tempo").EnumerateArray()
            .Select(p => (At: p[0].GetDouble(), Bpm: p[1].GetDouble())).OrderBy(p => p.At).ToArray();
        var loop = root.GetProperty("loop");
        return new ReaperChart
        {
            Project = root.GetProperty("project").GetString() ?? "",
            Repeat = root.GetProperty("repeat").GetInt32() != 0,
            LoopStart = loop[0].GetDouble(),
            LoopEnd = loop[1].GetDouble(),
            Events = events.OrderBy(e => e.At).ThenBy(e => e.Kind).ToArray(),
            Notes = NotesFrom(raw),
            Tempo = tempo,
            Ignored = ignored.ToArray(),
        };
    }

    static ChartEvent? Venue(double at, string t)
    {
        if (t == "[FogOn]") return new(at, ChartKind.Fog, 1);
        if (t == "[FogOff]") return new(at, ChartKind.Fog, 0);
        if (Effects.TryGetValue(t, out var effect)) return new(at, ChartKind.Effect, (int)effect);
        if (!t.StartsWith("[lighting (") || !t.EndsWith(")]")) return null;
        string name = t["[lighting (".Length..^2];
        if (Cues.TryGetValue(name, out var cue)) return new(at, ChartKind.Cue, (int)cue);
        if (Strobes.TryGetValue(name, out var strobe)) return new(at, ChartKind.Strobe, (int)strobe);
        return null;
    }

    /// <summary>Expert gems as the note bits YARG sends. A drum pad is a cymbal unless its tom marker
    /// (110 yellow, 111 blue, 112 green) is over the note.</summary>
    static ChartNote[] NotesFrom(List<(double At, double End, Instrument Part, int Pitch)> raw)
    {
        var toms = raw.Where(n => n.Part == Instrument.Drums && n.Pitch is >= 110 and <= 112).ToArray();
        bool Tom(int marker, double at) => toms.Any(m => m.Pitch == marker && m.At <= at && at < m.End);
        var notes = new List<ChartNote>();
        foreach (var n in raw)
        {
            int bits = (n.Part, n.Pitch) switch
            {
                (Instrument.Drums, 96) => (int)Drum.Kick,
                (Instrument.Drums, 97) => (int)Drum.Red,
                (Instrument.Drums, 98) => (int)(Tom(110, n.At) ? Drum.YellowPad : Drum.YellowCymbal),
                (Instrument.Drums, 99) => (int)(Tom(111, n.At) ? Drum.BluePad : Drum.BlueCymbal),
                (Instrument.Drums, 100) => (int)(Tom(112, n.At) ? Drum.GreenPad : Drum.GreenCymbal),
                (Instrument.Drums, _) => 0,
                (_, >= 96 and <= 100) => 2 << (n.Pitch - 96),     // green to orange
                _ => 0,
            };
            if (bits != 0)
                notes.Add(new(n.At, n.End, n.Part, (byte)bits));
        }
        return notes.OrderBy(n => n.At).ToArray();
    }

    public double BpmAt(double t)
    {
        double bpm = 0;
        foreach (var (at, value) in Tempo)
        {
            if (at > t && bpm > 0) break;
            bpm = value;
        }
        return bpm;
    }
}

/// <summary>Plays a chart from REAPER's transport. It doesn't work out what the lights would be at the
/// cursor: it's an editor's preview, so after a scrub or a stop the lights are dark until playback reaches
/// the next cue, and keyframes before that do nothing. A loop going round keeps the look running.</summary>
public sealed class ReaperPlayer
{
    const double JumpS = 0.25;           // further than this from where it should be: the cursor moved
    const double NearLoopS = 0.3;        // REAPER reports about 0.1 s apart, so a wrap lands this close

    readonly object _lock = new();
    readonly YargDecoder _decoder;
    ReaperChart? _chart;
    bool _chartAlive;
    bool _play, _pause, _wasStopped;
    bool _moving;                        // REAPER takes about 0.3 s to move after play: don't run ahead of it
    double _reported, _reportedAt;
    double _pos;                         // played up to here
    bool _reset = true, _wrap, _inclusive;

    public ReaperPlayer(YargDecoder decoder) => _decoder = decoder;

    bool Playing => _play && !_pause;

    /// <summary>A new export of the chart. Only another project starts over; an edit just plays on.</summary>
    public void SetChart(ReaperChart chart)
    {
        lock (_lock)
        {
            if (_chart != null && _chart.Project != chart.Project)
                _reset = true;
            _chart = chart;
        }
    }

    /// <summary>False when the export script has stopped refreshing the chart (it isn't running, or the open
    /// project has no VENUE track): the chart can't be trusted to be the one that's open, so it's not played.</summary>
    public void SetChartAlive(bool alive)
    {
        lock (_lock)
        {
            if (_chartAlive && !alive)
                _reset = true;
            _chartAlive = alive;
        }
    }

    /// <summary>The messages of one OSC packet, together: pausing arrives as /pause and /play at once.</summary>
    public void OnOsc(IEnumerable<(string Address, float Value)> messages, double now)
    {
        lock (_lock)
            foreach (var (address, value) in messages)
                switch (address)
                {
                    case "/play":
                        if (value > 0.5 != _play)
                            _moving = false;
                        _play = value > 0.5;
                        break;
                    case "/pause":
                        if (value > 0.5 != _pause)
                            _moving = false;
                        _pause = value > 0.5;
                        break;
                    case "/time":
                        OnTime(value, now);
                        break;
                }
    }

    double Estimate(double now) => _moving && Playing ? _reported + (now - _reportedAt) : _reported;

    void OnTime(double t, double now)
    {
        double expected = Estimate(now);
        if (Math.Abs(t - expected) > JumpS)
        {
            if (Playing && _chart is { Repeat: true } c && c.LoopEnd - c.LoopStart > 2 * NearLoopS
                && Math.Abs(expected - c.LoopEnd) < NearLoopS && Math.Abs(t - c.LoopStart) < NearLoopS)
                _wrap = true;
            else
                _reset = true;
        }
        else if (Playing && t > _reported)
            _moving = true;
        _reported = t;
        _reportedAt = now;
    }

    /// <summary>Moves the song on to <paramref name="now"/> and tells the decoder what happened.</summary>
    public void Tick(double now)
    {
        var events = new List<ChartEvent>();
        var held = new byte[4];
        var struck = new byte[4];
        bool reset, paused;
        double bpm = 0;
        string source;
        lock (_lock)
        {
            bool stopped = !_play && !_pause;
            if (stopped && !_wasStopped)
                _reset = true;
            _wasStopped = stopped;
            reset = _reset;
            paused = _pause;
            double pos = Estimate(now);
            if (reset || stopped)            // play picks up exactly at the cursor, even a cue right on it
            {
                (_reset, _wrap, _inclusive) = (false, false, true);
                _pos = pos;
            }
            var chart = _chartAlive ? _chart : null;
            if (chart != null && Playing && _moving)
            {
                if (_wrap)
                {
                    Collect(chart, _pos, chart.LoopEnd, _inclusive, events, struck);
                    (_pos, _inclusive, _wrap) = (chart.LoopStart, true, false);
                }
                if (pos > _pos || _inclusive)
                {
                    Collect(chart, _pos, pos, _inclusive, events, struck);
                    _pos = Math.Max(_pos, pos);
                    _inclusive = false;
                }
                foreach (var n in chart.Notes)
                {
                    if (n.At > pos) break;
                    if (pos < n.End) held[(int)n.Instrument] |= n.Bits;
                }
                for (int i = 0; i < 4; i++)
                    held[i] |= struck[i];
            }
            if (chart != null)
                bpm = chart.BpmAt(pos);
            source = chart == null ? "REAPER: no chart" : stopped ? "REAPER: stopped" : "REAPER";
        }
        _decoder.OnChart(now, source, reset, paused, bpm, events, held, struck);
    }

    static void Collect(ReaperChart chart, double from, double to, bool inclusive, List<ChartEvent> events, byte[] struck)
    {
        bool In(double at) => (inclusive ? at >= from : at > from) && at <= to;
        foreach (var e in chart.Events)
        {
            if (e.At > to) break;
            if (In(e.At)) events.Add(e);
        }
        foreach (var n in chart.Notes)
        {
            if (n.At > to) break;
            if (In(n.At)) struck[(int)n.Instrument] |= n.Bits;
        }
    }
}

/// <summary>The little of OSC that REAPER's position needs.</summary>
public static class Osc
{
    /// <summary>Each message in a packet whose first argument is a float, opening bundles. A malformed
    /// packet ends the list where it went wrong.</summary>
    public static List<(string Address, float Value)> Read(ReadOnlySpan<byte> packet)
    {
        var found = new List<(string, float)>();
        Read(packet, found);
        return found;
    }

    static void Read(ReadOnlySpan<byte> p, List<(string, float)> found)
    {
        if (p.StartsWith("#bundle\0"u8))
        {
            int i = 16;
            while (i + 4 <= p.Length)
            {
                int size = BinaryPrimitives.ReadInt32BigEndian(p[i..]);
                i += 4;
                if (size < 0 || size > p.Length - i)
                    return;
                Read(p.Slice(i, size), found);
                i += size;
            }
            return;
        }
        int at = 0;
        if (Text(p, ref at) is not { } address || Text(p, ref at) is not { } tags)
            return;
        if (tags.StartsWith(",f") && at + 4 <= p.Length)
            found.Add((address, BinaryPrimitives.ReadSingleBigEndian(p[at..])));
    }

    static string? Text(ReadOnlySpan<byte> p, ref int at)
    {
        if (at >= p.Length)
            return null;
        int end = p[at..].IndexOf((byte)0);
        if (end < 0)
            return null;
        var s = Encoding.UTF8.GetString(p.Slice(at, end));
        at += (end / 4 + 1) * 4;
        return s;
    }
}

/// <summary>Runs <see cref="ReaperPlayer"/> for real: listens for REAPER's OSC, and follows the file the
/// export script keeps rewriting (at least every couple of seconds, so an old file means it has stopped).</summary>
public sealed class ReaperInput : IDisposable
{
    public const int OscPort = 9000;
    public static string DefaultChartPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumenator", "reaper_chart.json");

    const double FileCheckS = 0.25, ChartStaleS = 6;

    readonly ReaperPlayer _player;
    readonly Stopwatch _clock;
    readonly string _chartPath;
    readonly UdpClient _udp;
    readonly CancellationTokenSource _stop = new();
    readonly Thread _thread;
    string _lastJson = "";
    string _lastProject = "\0";
    DateTime _lastWrite;

    /// <summary>The port it ended up on (given 0, any free one).</summary>
    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    /// <summary>The export script is running and has a chart for the open project.</summary>
    public bool ChartAlive { get; private set; }

    public ReaperInput(YargDecoder decoder, Stopwatch clock, int port = OscPort, string? chartPath = null)
    {
        _player = new ReaperPlayer(decoder);
        _clock = clock;
        _chartPath = chartPath ?? DefaultChartPath;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        _ = Task.Run(Listen);
        _thread = new Thread(Run) { IsBackground = true, Name = "reaper" };
        _thread.Start();
    }

    async Task Listen()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }
            _player.OnOsc(Osc.Read(r.Buffer), _clock.Elapsed.TotalSeconds);
        }
    }

    void Run()
    {
        double checkAt = 0;
        while (!_stop.IsCancellationRequested)
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (now >= checkAt)
            {
                checkAt = now + FileCheckS;
                CheckChart();
            }
            _player.Tick(now);
            Thread.Sleep(5);
        }
    }

    void CheckChart()
    {
        try
        {
            var write = File.GetLastWriteTimeUtc(_chartPath);
            ChartAlive = File.Exists(_chartPath) && (DateTime.UtcNow - write).TotalSeconds < ChartStaleS;
            _player.SetChartAlive(ChartAlive);
            if (write == _lastWrite)
                return;
            var json = File.ReadAllText(_chartPath);
            _lastWrite = write;
            if (json == _lastJson)
                return;
            var chart = ReaperChart.Parse(json);
            _lastJson = json;
            _player.SetChart(chart);
            if (chart.Project != _lastProject)
            {
                _lastProject = chart.Project;
                Console.WriteLine($"REAPER chart: {Path.GetFileName(chart.Project)} ({chart.Events.Count} events, {chart.Notes.Count} notes)"
                    + (chart.Ignored.Count > 0 ? $"; ignoring {string.Join(" ", chart.Ignored)}" : ""));
            }
        }
        // mid-rewrite: the last good chart plays on, and the next look picks it up
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(500);
        _udp.Dispose();
    }
}
