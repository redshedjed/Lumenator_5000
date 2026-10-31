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
