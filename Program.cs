// Lumenator 5000: reads YARG's lighting data (its UDP data stream, or sACN), interprets it with the
// looks in rig.json (cue -> look; beat, keyframe and drum triggers), and sends the result to DMX: an
// FTDI USB-DMX cable, sACN, and/or the browser visualizer. Started from Neal Manson's YARG_sACN_to_DMX,
// which mapped YARG's Stage Kit lights straight onto 2/4/8 PARs (the "stagekit" look still does).
//
//   Lumenator [--rig rig.json] [--sim | --reaper] [--port 8080] [--no-visualizer]
//     --sim      fake YARG: a looping demo song sent as real sACN (no game needed)
//     --reaper   follow a chart being edited in REAPER instead of YARG
//   Without either, the input is rig.json's, which the visualizer's input menu sets.

using System.Diagnostics;
using YargDmx;
using YargDmx.Engine;
using YargDmx.Input;
using YargDmx.Output;
using YargDmx.Visualizer;

// the rig.json you're editing (current folder) wins over the copy next to the exe
string rigPath = File.Exists("rig.json") ? Path.GetFullPath("rig.json") : Path.Combine(AppContext.BaseDirectory, "rig.json");
bool sim = false, reaper = false, visualizer = true;
int? port = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--rig": rigPath = Path.GetFullPath(args[++i]); break;
        case "--sim": sim = true; break;
        case "--reaper": reaper = true; break;
        case "--port": port = int.Parse(args[++i]); break;
        case "--no-visualizer": visualizer = false; break;
        default:
            Console.WriteLine("usage: Lumenator [--rig rig.json] [--sim | --reaper] [--port 8080] [--no-visualizer]");
            return 2;
    }
}
if (sim && reaper)
{
    Console.WriteLine("--sim and --reaper are different inputs: pick one");
    return 2;
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

// one input at a time: YARG, the simulator or a chart in REAPER. A command-line choice lasts until the menu
// or rig.json changes it.
string? inputChoice = sim ? "sim" : reaper ? "reaper" : null;
string inputKind = "", inputProblem = "";
E131Receiver? receiver = null;
DataStreamReceiver? stream = null;
YargSimulator? simulator = null;
ReaperInput? reaperInput = null;
var reaperSetup = new ReaperSetup();
string WantedInput() => inputChoice ?? rig.Input;

void CloseInput()
{
    reaperInput?.Dispose();
    simulator?.Dispose();
    stream?.Dispose();
    receiver?.Dispose();
    (reaperInput, simulator, stream, receiver) = (null, null, null, null);
}

void OpenInput(string kind)
{
    CloseInput();
    inputKind = kind;
    inputProblem = "";
    decoder = new YargDecoder(rig.Channels, clock);      // nothing carries over from the last input
    try
    {
        if (kind == "reaper")
        {
            TryRefreshReaperScript();
            reaperInput = new ReaperInput(decoder, clock);
            Console.WriteLine($"following REAPER: OSC on UDP {ReaperInput.OscPort}, chart from {ReaperInput.DefaultChartPath}");
            return;
        }
        receiver = new E131Receiver(rig.Universe, decoder.OnFrame);
        Console.WriteLine($"listening for YARG on sACN universe {rig.Universe}");
        if (kind == "sim")
        {
            simulator = new YargSimulator(rig.Channels, rig.Universe);
            receiver.Sources.Only = simulator.SourceId;  // not YARG too, if it's running
            Console.WriteLine("simulator: sending a demo song as YARG would");
            return;
        }
    }
    catch (System.Net.Sockets.SocketException e)
    {
        inputProblem = kind == "reaper" ? $"Can't listen on UDP {ReaperInput.OscPort}: {e.Message}" : $"Can't listen for sACN: {e.Message}";
        Console.WriteLine(inputProblem);
        return;
    }
    // YARG's UDP data stream, when it's switched on in YARG: tempo, pause, spotlights... and no clashing channels
    try
    {
        stream = new DataStreamReceiver(decoder.OnDataStream);
        Console.WriteLine($"listening for YARG's data stream on UDP {DataStreamPacket.Port}");
    }
    catch (System.Net.Sockets.SocketException e)
    {
        Console.WriteLine($"data stream: can't listen on UDP {DataStreamPacket.Port} ({e.Message}); sACN only");
    }
}

void TryRefreshReaperScript()
{
    try { reaperSetup.Refresh(); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine($"REAPER: couldn't update the script ({e.Message})");
    }
}

OpenInput(WantedInput());

var outputs = new List<IDmxOutput>();
var outputProblems = new List<string>();      // shown in the visualizer's output menu
double usbRetryAt = double.PositiveInfinity;  // a chosen adapter that isn't there is tried again every few s
bool WantsUsb(OutputConfig o) => o.UsbAdapter.Length > 0 || o.Ftdi;
string UsbProblem(Exception e) => e is DllNotFoundException or EntryPointNotFoundException
    ? "USB: FTDI's driver isn't installed (ftd2xx.dll)" : $"USB: {e.Message}";
void OpenOutputs()
{
    lock (outputs)
    {
        foreach (var o in outputs) o.Dispose();
        outputs.Clear();
        outputProblems.Clear();
        usbRetryAt = double.PositiveInfinity;
        if (WantsUsb(rig.Output))
            TryUsb();
        if (rig.Output.SacnUniverse != 0)
        {
            if (rig.Output.SacnUniverse == rig.Universe)
                outputProblems.Add($"sACN: can't send on YARG's own universe ({rig.Universe})");
            else
                outputs.Add(new SacnOutput(rig.Output.SacnUniverse));
        }
        Console.WriteLine("outputs: " + (outputs.Count > 0 ? string.Join(", ", outputs.Select(o => o.Name)) : "visualizer only")
            + string.Concat(outputProblems.Select(p => $" ({p})")));
    }
}
// (with outputs locked) open the chosen USB adapter, or note why not and try again in a few seconds
bool TryUsb()
{
    outputProblems.RemoveAll(p => p.StartsWith("USB:"));
    try
    {
        outputs.Insert(0, new FtdiOutput(rig.Output.UsbAdapter));
        usbRetryAt = double.PositiveInfinity;
        return true;
    }
    catch (Exception e) when (e is IOException or DllNotFoundException or EntryPointNotFoundException)
    {
        outputProblems.Add(UsbProblem(e));
        usbRetryAt = clock.Elapsed.TotalSeconds + 3;
        return false;
    }
}
OpenOutputs();

using var viz = visualizer ? new VisualizerServer(port ?? rig.Output.VisualizerPort) : null;
if (viz != null) Console.WriteLine($"visualizer: {viz.Url}");

// the visualizer's light editor: it saves the fixture list into rig.json (picked up by the reload below)
// and can flash one light to find it
var rigWrite = new object();
(int Index, double Until) identify = (-1, 0);
if (viz != null)
{
    viz.OnFixtures = body =>
    {
        var list = System.Text.Json.JsonSerializer.Deserialize<List<Fixture>>(body,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        if (list.Any(f => string.IsNullOrWhiteSpace(f.Name)))
            return "every light needs a name";
        lock (rigWrite)
        {
            try
            {
                var text = RigFile.ReplaceFixtures(File.ReadAllText(rigPath), list);
                Rig.Parse(text);                          // only write a rig that loads
                File.WriteAllText(rigPath, text);
                // brightness takes effect now, not when the reload settles (a slider saves as it moves)
                var current = rig;
                if (current.Fixtures.Count == list.Count)
                    for (int i = 0; i < list.Count; i++)
                        if (current.Fixtures[i].Name == list[i].Name)
                            current.Fixtures[i].Level = list[i].Level;
                return null;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException)
            {
                return e.Message;
            }
        }
    };
    viz.OnIdentify = i => identify = (i, clock.Elapsed.TotalSeconds + 3);    // -1 stops it
    viz.OnMaster = value =>
    {
        value = Math.Clamp(value, 0, 1);
        lock (rigWrite)
        {
            try
            {
                var text = RigFile.SetNumber(File.ReadAllText(rigPath), "master", value);
                Rig.Parse(text);
                File.WriteAllText(rigPath, text);
                rig.Master = value;
                return null;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException)
            {
                return e.Message;
            }
        }
    };
    viz.OnManual = bits => show.Manual = bits;
    // the input menu: saved into rig.json, and the main loop switches
    viz.OnInput = value =>
    {
        lock (rigWrite)
        {
            try
            {
                var text = RigFile.SetText(File.ReadAllText(rigPath), "input", value);
                Rig.Parse(text);
                File.WriteAllText(rigPath, text);
                inputChoice = value;
                return null;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException)
            {
                return e.Message;
            }
        }
    };
    // what the menu shows for REAPER, and what it can do about it
    viz.OnReaper = action =>
    {
        string? error = null;
        try
        {
            if (action == "install") reaperSetup.Install();
            else if (action == "remove") reaperSetup.Remove();
            var (sends, acceptsCommands) = reaperSetup.Osc();
            return new { ok = true, error, reaperFound = reaperSetup.ReaperFound, installed = reaperSetup.Installed, oscSends = sends,
                         oscAcceptsCommands = acceptsCommands, chartLive = reaperInput?.ChartAlive ?? false };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new { ok = false, error = (string?)e.Message, reaperFound = false, installed = false, oscSends = false,
                         oscAcceptsCommands = false, chartLive = false };
        }
    };
    viz.OnUsbDevices = () =>
    {
        try { return new { ok = true, error = (string?)null, devices = FtdiOutput.Devices().Select(d => new { serial = d.Serial, description = d.Description }) }; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return new { ok = false, error = (string?)"FTDI's driver isn't installed (ftd2xx.dll)", devices = Enumerable.Empty<object>().Select(_ => new { serial = "", description = "" }) };
        }
    };
    // the output menu: saved into rig.json's "output" section, so the reload below opens them
    viz.OnOutputs = body =>
    {
        var o = System.Text.Json.JsonDocument.Parse(body).RootElement;
        bool preview = o.GetProperty("visualizer").GetBoolean();
        string usb = o.GetProperty("usb").GetString() ?? "";
        int sacn = o.GetProperty("sacn").GetInt32();
        if (sacn is < 0 or > 63999)
            return "sACN universe is 1 to 63999";
        if (sacn != 0 && sacn == rig.Universe)
            return $"universe {sacn} is the one YARG sends on; pick another";
        lock (rigWrite)
        {
            try
            {
                var text = File.ReadAllText(rigPath);
                text = RigFile.SetIn(text, "output", "visualizer", preview);
                text = RigFile.SetIn(text, "output", "usb_adapter", usb);
                text = RigFile.SetIn(text, "output", "ftdi", false);          // the adapter is chosen by name now
                text = RigFile.SetIn(text, "output", "sacn_universe", sacn);
                Rig.Parse(text);
                File.WriteAllText(rigPath, text);
                return null;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException)
            {
                return e.Message;
            }
        }
    };
}

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
            bool outputsChanged = next.Output.Ftdi != rig.Output.Ftdi || next.Output.UsbAdapter != rig.Output.UsbAdapter
                || next.Output.SacnUniverse != rig.Output.SacnUniverse || next.Universe != rig.Universe;
            bool universeChanged = next.Universe != rig.Universe;
            if (next.Input != rig.Input)
                inputChoice = null;                      // rig.json's own choice takes over
            rig = next;
            show.Rig = rig;
            decoder.Map = rig.Channels;
            frameMs = 1000 / Math.Clamp(rig.Output.Fps, 10, 60);
            if (outputsChanged) OpenOutputs();
            if (universeChanged && inputKind != "reaper") OpenInput(WantedInput());
            Console.WriteLine($"rig reloaded ({rig.Fixtures.Count} fixtures, {rig.Looks.Count} looks)");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException or KeyNotFoundException)
        {
            Console.WriteLine($"rig.json not reloaded: {e.Message}");
        }
    }

    if (WantedInput() != inputKind)
        OpenInput(WantedInput());

    double now = clock.Elapsed.TotalSeconds;
    if (now >= usbRetryAt && WantsUsb(rig.Output))
        lock (outputs)
            if (TryUsb()) Console.WriteLine($"outputs: {outputs[0].Name} connected");
    var state = decoder.Snapshot();
    var colours = show.Render(state, now);
    bool quiet = now - state.LastFrameAt > 3;
    if (quiet && show.Manual == null)
        Array.Fill(colours, Rgb.Black);           // YARG isn't sending: dark, not a frozen look
    if (identify is var (who, until) && now < until && who >= 0 && who < colours.Length)
        colours[who] = Rgb.White;
    var universe = Universe.Build(rig, colours, state.Fog);
    foreach (var o in outputs) o.Send(universe);

    if (viz != null)
    {
        var shown = Universe.Read(rig, universe);
        viz.Publish(new
        {
            layout = string.Join("|", rig.Fixtures.Select(f => $"{f.Name}@{f.X},{f.Y}#{f.Address}/{f.Profile}")),
            fixtures = rig.Fixtures.Select(f => new { name = f.Name, x = f.X, y = f.Y, address = f.Address, profile = f.Profile, level = f.Level }),
            master = rig.Master,
            manual = show.Manual,
            profiles = rig.Profiles.ToDictionary(p => p.Key, p => p.Value.Width),
            colours = rig.Output.Visualizer
                ? shown.Select(c => new[] { (int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255) })
                : null,
            input = inputProblem.Length > 0 ? "no input" : inputKind == "reaper" ? state.Source
                : quiet ? "waiting" : state.Source == "data stream" ? "YARG data stream"
                : receiver?.Sources.Source is { Length: > 0 } src ? src : inputKind == "sim" ? "simulator" : "YARG",
            inputKind,
            inputProblem,
            paused = state.Paused,
            cue = state.Waiting ? "waiting for a cue" : state.Cue.ToString(),
            look = show.LookName,
            effectDoes = rig.PostProcessing.TryGetValue(state.PostProcessing.ToString(), out var fx) ? Describe(fx) : "",
            strobe = state.Strobe.ToString(),
            fog = state.Fog,
            effect = state.PostProcessing.ToString(),
            bpm = state.Bpm > 0 ? state.Bpm : now - state.LastAt[(int)Trigger.Beat] < 3 ? 60 / state.BeatPeriod : 0,
            outputs = string.Join(", ", (rig.Output.Visualizer ? new[] { "visualizer" } : Array.Empty<string>())
                .Concat(outputs.Select(o => o.Name)).DefaultIfEmpty("none")),
            output = new
            {
                visualizer = rig.Output.Visualizer,
                usb = rig.Output.UsbAdapter.Length > 0 ? rig.Output.UsbAdapter : rig.Output.Ftdi ? "(first found)" : "",
                sacn = rig.Output.SacnUniverse,
                yargUniverse = rig.Universe,
                problems = outputProblems.ToArray(),
            },
        });
    }
    try { await Task.Delay(frameMs, stop.Token); }
    catch (OperationCanceledException) { }
}

CloseInput();
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
