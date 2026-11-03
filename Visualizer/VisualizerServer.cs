using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace YargDmx.Visualizer;

/// <summary>The fake DMX output: a page on http://localhost:port/ that draws each fixture from the
/// universe being sent, plus what YARG is saying. <see cref="Publish"/> sets the latest frame; every open
/// page gets it as a server-sent event.</summary>
public sealed class VisualizerServer : IDisposable
{
    readonly HttpListener _http = new();
    readonly List<HttpListenerResponse> _clients = new();
    readonly object _lock = new();
    readonly byte[] _page;
    string _latest = "{}";
    readonly Timer _push;

    public string Url { get; }
    /// <summary>POST /api/fixtures: the page's light list (JSON); returns an error message, or null when saved.</summary>
    public Func<string, string?>? OnFixtures { get; set; }
    /// <summary>POST /api/identify: flash one fixture (by index).</summary>
    public Action<int>? OnIdentify { get; set; }
    /// <summary>POST /api/master: the master brightness (0-1); returns an error message, or null when saved.</summary>
    public Func<double, string?>? OnMaster { get; set; }
    /// <summary>POST /api/manual: each light's colour bits (red 1, green 2, blue 4), or null to give the
    /// lights back to YARG.</summary>
    public Action<int[]?>? OnManual { get; set; }
    /// <summary>POST /api/outputs: the output settings (JSON); returns an error message, or null when saved.</summary>
    public Func<string, string?>? OnOutputs { get; set; }
    /// <summary>POST /api/usb: the USB-DMX adapters plugged in, as the reply.</summary>
    public Func<object>? OnUsbDevices { get; set; }

    public VisualizerServer(int port)
    {
        Url = $"http://localhost:{port}/";
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        _page = ms.ToArray();
        _http.Prefixes.Add(Url);
        _http.Start();
        _ = Task.Run(Accept);
        _push = new Timer(_ => Push(), null, 0, 33);
    }

    public void Publish(object frame) => Volatile.Write(ref _latest, JsonSerializer.Serialize(frame));

    async Task Accept()
    {
        while (_http.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch (Exception) { return; }
            var r = ctx.Response;
            if (ctx.Request.Url?.AbsolutePath.StartsWith("/api/") == true)
            {
                _ = Task.Run(() => Api(ctx));
                continue;
            }
            if (ctx.Request.Url?.AbsolutePath == "/events")
            {
                r.ContentType = "text/event-stream";
                r.Headers["Cache-Control"] = "no-cache";
                r.SendChunked = true;
                lock (_lock) _clients.Add(r);
            }
            else
            {
                r.ContentType = "text/html; charset=utf-8";
                r.ContentLength64 = _page.Length;
                await r.OutputStream.WriteAsync(_page);
                r.Close();
            }
        }
    }

    /// <summary>Edits from the page. Only from this PC, only JSON (a browser won't send that cross-site
    /// without a preflight, which this server never grants), and only from the visualizer's own page.</summary>
    async Task Api(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        string? error;
        object? reply = null;                       // a reply with data, instead of just { ok, error }
        int status = 400;
        var origin = req.Headers["Origin"];
        if (!req.IsLocal || req.HttpMethod != "POST" || req.ContentType?.StartsWith("application/json") != true ||
            (origin != null && origin.TrimEnd('/') != Url.TrimEnd('/')))
        {
            error = "not allowed";
            status = 403;
        }
        else
        {
            string body;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync();
            try
            {
                switch (req.Url!.AbsolutePath)
                {
                    case "/api/fixtures":
                        error = OnFixtures?.Invoke(body) ?? null;
                        break;
                    case "/api/identify":
                        OnIdentify?.Invoke(JsonDocument.Parse(body).RootElement.GetProperty("index").GetInt32());
                        error = null;
                        break;
                    case "/api/master":
                        error = OnMaster?.Invoke(JsonDocument.Parse(body).RootElement.GetProperty("value").GetDouble());
                        break;
                    case "/api/outputs":
                        error = OnOutputs?.Invoke(body);
                        break;
                    case "/api/usb":
                        reply = OnUsbDevices?.Invoke();
                        error = null;
                        break;
                    case "/api/manual":
                        var bits = JsonDocument.Parse(body).RootElement.GetProperty("lights");
                        OnManual?.Invoke(bits.ValueKind == JsonValueKind.Null ? null
                            : bits.EnumerateArray().Select(b => b.GetInt32() & 7).ToArray());
                        error = null;
                        break;
                    default:
                        error = "no such action";
                        status = 404;
                        break;
                }
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                error = "bad request: " + e.Message;
            }
        }
        var bytes = Encoding.UTF8.GetBytes(error == null && reply != null ? JsonSerializer.Serialize(reply)
            : JsonSerializer.Serialize(error == null ? new { ok = true, error = (string?)null } : new { ok = false, error = (string?)error }));
        var r = ctx.Response;
        r.StatusCode = error == null ? 200 : status;
        r.ContentType = "application/json";
        r.ContentLength64 = bytes.Length;
        try { await r.OutputStream.WriteAsync(bytes); r.Close(); }
        catch (Exception) { }
    }

    void Push()
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {Volatile.Read(ref _latest)}\n\n");
        HttpListenerResponse[] clients;
        lock (_lock) clients = _clients.ToArray();
        foreach (var c in clients)
        {
            try
            {
                c.OutputStream.Write(bytes);
                c.OutputStream.Flush();
            }
            catch (Exception)
            {
                lock (_lock) _clients.Remove(c);
                try { c.Abort(); } catch (Exception) { }
            }
        }
    }

    public void Dispose()
    {
        _push.Dispose();
        lock (_lock)
            foreach (var c in _clients)
                try { c.Abort(); } catch (Exception) { }
        _http.Close();
    }
}
