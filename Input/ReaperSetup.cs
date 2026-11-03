using System.Reflection;
using System.Text.RegularExpressions;

namespace YargDmx.Input;

/// <summary>What <c>--reaper</c> needs from REAPER, and the part Lumenator can do for you: it keeps the export
/// script (built into this program) in REAPER's Scripts folder and starts it with REAPER, from a line in
/// <c>__startup.lua</c>, leaving anything else there alone. REAPER's OSC setting is only read, never changed:
/// REAPER rewrites its own settings, so the visualizer tells you what to set.</summary>
public sealed class ReaperSetup
{
    const string ScriptName = "lumenator_export.lua";
    const string StartupLine = "dofile(reaper.GetResourcePath() .. \"/Scripts/lumenator_export.lua\") -- Lumenator 5000";

    readonly string _folder;

    public ReaperSetup(string? reaperFolder = null) =>
        _folder = reaperFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "REAPER");

    string Ini => Path.Combine(_folder, "reaper.ini");
    string Scripts => Path.Combine(_folder, "Scripts");
    string ScriptPath => Path.Combine(Scripts, ScriptName);
    string StartupPath => Path.Combine(Scripts, "__startup.lua");

    public static string Script
    {
        get
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ScriptName)!;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
    }

    public bool ReaperFound => File.Exists(Ini);

    public bool Installed =>
        File.Exists(ScriptPath) && File.Exists(StartupPath) && File.ReadAllLines(StartupPath).Contains(StartupLine);

    /// <summary>Puts the script in place and has REAPER start it. Takes effect the next time REAPER starts.</summary>
    public void Install()
    {
        if (!ReaperFound)
            throw new IOException($"REAPER's settings aren't in {_folder}");
        Directory.CreateDirectory(Scripts);
        File.WriteAllText(ScriptPath, Script);
        if (Installed)
            return;
        var existing = File.Exists(StartupPath) ? File.ReadAllText(StartupPath) : "";
        var gap = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
        File.WriteAllText(StartupPath, existing + gap + StartupLine + "\n");
    }

    /// <summary>Removes the script and its line. A <c>__startup.lua</c> with nothing else in it goes too.</summary>
    public void Remove()
    {
        if (File.Exists(StartupPath))
        {
            var kept = File.ReadAllLines(StartupPath).Where(l => l != StartupLine).ToArray();
            if (kept.All(string.IsNullOrWhiteSpace))
                File.Delete(StartupPath);
            else
                File.WriteAllLines(StartupPath, kept);
        }
        if (File.Exists(ScriptPath))
            File.Delete(ScriptPath);
    }

    /// <summary>If it's installed, brings the script up to this program's version.</summary>
    public void Refresh()
    {
        if (Installed && File.ReadAllText(ScriptPath) != Script)
            File.WriteAllText(ScriptPath, Script);
    }

    /// <summary>The OSC control surface in reaper.ini sending to Lumenator, if any: Sends is whether one sends
    /// to this PC on our port, and AcceptsCommands whether it's the mode (3, "device IP + local port") that
    /// also listens on the network, where anyone who can reach it can trigger REAPER's actions.</summary>
    public (bool Sends, bool AcceptsCommands) Osc()
    {
        if (!ReaperFound)
            return (false, false);
        foreach (var line in File.ReadLines(Ini))
        {
            var m = Regex.Match(line, @"^csurf_\d+=OSC\s+""[^""]*""\s+(\d+)\s+\d+\s+""([^""]*)""\s+(\d+)");
            if (m.Success && m.Groups[2].Value == "127.0.0.1" && m.Groups[3].Value == ReaperInput.OscPort.ToString())
                return (true, m.Groups[1].Value == "3");
        }
        return (false, false);
    }
}
