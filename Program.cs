// Reads YARG's sACN lighting data, interprets it with the looks in rig.json (cue -> look; beat,
// keyframe and drum triggers), and sends the result to DMX: an FTDI USB-DMX cable, sACN, and/or the
// browser visualizer (fake DMX output). Based on Neal Manson's YARG_sACN_to_DMX, which mapped YARG's
// Stage Kit lights straight onto 2/4/8 PARs (the "stagekit" look still does).
//
//   YARG_sACN_to_DMX [--rig rig.json] [--sim] [--port 8080] [--no-visualizer]
//     --sim   fake YARG: a looping demo song sent as real sACN (no game needed)

using System.Diagnostics;
using YargDmx;
using YargDmx.Engine;
using YargDmx.Input;
using YargDmx.Output;
using YargDmx.Visualizer;

// the rig.json you're editing (current folder) wins over the copy next to the exe
string rigPath = File.Exists("rig.json") ? Path.GetFullPath("rig.json") : Path.Combine(AppContext.BaseDirectory, "rig.json");
bool sim = false, visualizer = true;
int? port = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--rig": rigPath = Path.GetFullPath(args[++i]); break;
        case "--sim": sim = true; break;
        case "--port": port = int.Parse(args[++i]); break;
        case "--no-visualizer": visualizer = false; break;
        default:
            Console.WriteLine("usage: YARG_sACN_to_DMX [--rig rig.json] [--sim] [--port 8080] [--no-visualizer]");
            return 2;
    }
}

Rig rig;
try { rig = Rig.Load(rigPath); }
catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException)
{
    Console.WriteLine($"{rigPath}: {e.Message}");
    return 1;
}
Console.WriteLine($"rig: {rigPath} ({rig.Fixtures.Count} fixtures, {rig.Looks.Count} looks)");

var clock = Stopwatch.StartNew();
var decoder = new YargDecoder(rig.Channels, clock);
var show = new Show(rig);
using var receiver = new E131Receiver(rig.Universe, decoder.OnFrame);
Console.WriteLine($"listening for YARG on sACN universe {rig.Universe}");
using var simulator = sim ? new YargSimulator(rig.Channels, rig.Universe) : null;
if (simulator != null)
{
    receiver.Sources.Only = simulator.SourceId;      // not YARG too, if it's running
    Console.WriteLine("simulator: sending a demo song as YARG would");
}

var outputs = new List<IDmxOutput>();
void OpenOutputs()
{
    foreach (var o in outputs) o.Dispose();
    outputs.Clear();
    if (rig.Output.Ftdi)
    {
        try { outputs.Add(new FtdiOutput()); }
        catch (Exception e) when (e is IOException or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.WriteLine($"FTDI: {e.Message} (no DMX cable output)");
        }
    }
    if (rig.Output.SacnUniverse != 0)
    {
        if (rig.Output.SacnUniverse == rig.Universe)
            Console.WriteLine("sACN output: can't send on YARG's own universe; skipped");
        else
            outputs.Add(new SacnOutput(rig.Output.SacnUniverse));
    }
    Console.WriteLine("outputs: " + (outputs.Count > 0 ? string.Join(", ", outputs.Select(o => o.Name)) : "visualizer only"));
}
OpenOutputs();

using var viz = visualizer ? new VisualizerServer(port ?? rig.Output.VisualizerPort) : null;
if (viz != null) Console.WriteLine($"visualizer: {viz.Url}");

// tuning: save rig.json and it's picked up (a broken file keeps the last good rig)
var reload = new FileSystemWatcher(Path.GetDirectoryName(rigPath)!, Path.GetFileName(rigPath))
{
    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
    EnableRaisingEvents = true,
};
long reloadAt = 0;
void Reload(object? _, FileSystemEventArgs e) => Interlocked.Exchange(ref reloadAt, clock.ElapsedMilliseconds + 200);
reload.Changed += Reload;
reload.Created += Reload;                      // editors that save by replacing the file
reload.Renamed += (s, e) => Reload(s, e);

var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.WriteLine("Ctrl+C to stop");

var collisions = rig.Channels.Collisions(ChannelMap.YargFog, ChannelMap.YargStrobe).ToList();
string layoutKey = "";
int frameMs = 1000 / Math.Clamp(rig.Output.Fps, 10, 60);
while (!stop.IsCancellationRequested)
{
    long due = Interlocked.Read(ref reloadAt);
    if (due != 0 && clock.ElapsedMilliseconds >= due)
    {
        Interlocked.Exchange(ref reloadAt, 0);
        try
        {
            var next = Rig.Load(rigPath);
            bool outputsChanged = next.Output.Ftdi != rig.Output.Ftdi || next.Output.SacnUniverse != rig.Output.SacnUniverse;
            rig = next;
            show.Rig = rig;
            decoder.Map = rig.Channels;
            collisions = rig.Channels.Collisions(ChannelMap.YargFog, ChannelMap.YargStrobe).ToList();
            frameMs = 1000 / Math.Clamp(rig.Output.Fps, 10, 60);
            if (outputsChanged) OpenOutputs();
            Console.WriteLine($"rig reloaded ({rig.Fixtures.Count} fixtures, {rig.Looks.Count} looks)");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException or KeyNotFoundException)
        {
            Console.WriteLine($"rig.json not reloaded: {e.Message}");
        }
    }

    double now = clock.Elapsed.TotalSeconds;
    var state = decoder.Snapshot();
    var colours = show.Render(state, now);
    var universe = Universe.Build(rig, colours, state.Fog);
    foreach (var o in outputs) o.Send(universe);

    if (viz != null)
    {
        var notes = new List<string>();
        if (now - state.LastFrameAt > 3)
            notes.Add(sim ? "no sACN from the simulator" : "no sACN from YARG: Settings > All Settings > Lighting > Enable DMX output (universe " + rig.Universe + ")");
        var ignored = receiver.Sources.Ignored(Environment.TickCount64);
        if (ignored.Count > 0)
            notes.Add("also on universe " + rig.Universe + ", ignored: " + string.Join(", ", ignored.Select(n => $"\"{n}\"")) +
                      (sim ? " (running with --sim)" : ""));
        if (state.Fog && collisions.Count > 0)
            notes.Add("fog is on and YARG's default channels put fog on top of " + string.Join("; ", collisions) +
                      ": drum / beat triggers are unreliable. Move the game channels in YARG and in rig.json (channels).");
        var key = string.Join("|", rig.Fixtures.Select(f => $"{f.Name}@{f.X},{f.Y}"));
        layoutKey = key;
        var shown = Universe.Read(rig, universe);
        viz.Publish(new
        {
            layout = layoutKey,
            fixtures = rig.Fixtures.Select(f => new { name = f.Name, x = f.X, y = f.Y }),
            colours = shown.Select(c => new[] { (int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255) }),
            dmx = rig.Fixtures.Select(f => $"@{f.Address}: " + string.Join(" ",
                universe.Skip(f.Address - 1).Take(rig.Profiles[f.Profile].Width))),
            input = now - state.LastFrameAt > 3 ? "waiting" : receiver.Sources.Source is { Length: > 0 } src ? src : sim ? "simulator" : "YARG",
            cue = state.Cue.ToString(),
            look = show.LookName,
            effectDoes = rig.PostProcessing.TryGetValue(state.PostProcessing.ToString(), out var fx) ? Describe(fx) : "",
            strobe = state.Strobe.ToString(),
            fog = state.Fog,
            effect = state.PostProcessing.ToString(),
            held = state.Held.Select(b => (int)b),
            bpm = now - state.LastAt[(int)Trigger.Beat] < 3 ? 60 / state.BeatPeriod : 0,
            outputs = outputs.Count > 0 ? string.Join(", ", outputs.Select(o => o.Name)) : "visualizer only",
            counts = Enum.GetValues<Trigger>().ToDictionary(t => t.ToString(), t => state.Count(t)),
            kit = Enumerable.Range(0, 4).Select(c => Enumerable.Range(0, 8).Select(i => state.Leds[c, i])),
            notes,
        });
    }
    try { await Task.Delay(frameMs, stop.Token); }
    catch (OperationCanceledException) { }
}

foreach (var o in outputs) o.Dispose();
Console.WriteLine("stopped");
return 0;

// what a screen effect does to the rig, in a few words for the visualizer
static string Describe(PostEffect fx)
{
    var parts = new List<string>();
    if (fx.Tint is { Length: > 0 }) parts.Add(fx.Strength >= 1 ? $"tint {fx.Tint}" : $"tint {fx.Tint} {fx.Strength:0%}");
    if (fx.Mirror) parts.Add("mirror");
    if (fx.Invert) parts.Add("invert");
    if (fx.Polarize.Count > 0) parts.Add("polarize " + string.Join("/", fx.Polarize));
    if (fx.Posterize) parts.Add("posterize");
    if (fx.Contrast > 0) parts.Add("contrast");
    if (fx.Lift > 0) parts.Add("lift");
    if (fx.Scanline > 0) parts.Add("scanline");
    if (fx.Flicker > 0) parts.Add("flicker");
    if (fx.Fringe > 0) parts.Add("fringe");
    if (fx.Trails > 0) parts.Add($"trails {fx.Trails:0.##} s");
    if (fx.Choppy > 0) parts.Add($"choppy {fx.Choppy:0} fps");
    return string.Join(" · ", parts);
}
