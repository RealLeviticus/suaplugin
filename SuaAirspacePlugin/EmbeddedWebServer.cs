using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SuaAirspacePlugin;

public sealed class EmbeddedWebServer : IDisposable
{
    private static readonly byte[] HeaderEnd = { 13, 10, 13, 10 };

    private readonly SuaAirspaceService _sua;
    private readonly SuaNotamService _notam;
    private readonly PluginConfig _config;
    private readonly JavaScriptSerializer _json;
    private readonly CancellationTokenSource _cts;
    private readonly int _port;

    private readonly object _lock = new object();
    private TcpListener? _listener;
    private Task? _loop;
    private bool _disposed;

    public EmbeddedWebServer(SuaAirspaceService sua, SuaNotamService notam, PluginConfig config, int port)
    {
        _sua = sua;
        _notam = notam;
        _config = config;
        _port = port;
        _json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        _cts = new CancellationTokenSource();
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EmbeddedWebServer));
            if (_listener is not null) return;
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _loop = Task.Run(AcceptLoop);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        lock (_lock) { try { _listener?.Stop(); } catch { } _listener = null; }
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                var l = _listener;
                if (l is null) return;
                client = await l.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleClient(client));
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (_cts.IsCancellationRequested) return; }
            catch { try { client?.Dispose(); } catch { } }
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        {
            NetworkStream? stream = null;
            try
            {
                // A client that connects but never completes a request must not
                // hold a worker forever.
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;
                stream = client.GetStream();
                var req = ReadRequest(stream);
                if (req is not null) Route(stream, req);
            }
            catch
            {
                if (stream is not null)
                    try { WriteJson(stream, 500, new { Error = "Server error." }); } catch { }
            }
        }
    }

    private void Route(NetworkStream s, HttpReq req)
    {
        if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            WriteResponse(s, 204, "text/plain", Array.Empty<byte>());
            return;
        }

        if (req.Path == "/" || req.Path == "/index.html" || req.Path == "/sua" || req.Path == "/sua/")
        {
            if (!string.IsNullOrWhiteSpace(_config.PublicUiUrl))
            {
                WriteRedirect(s, _config.PublicUiUrl);
                return;
            }
            WriteResponse(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(SuaUiPage.Html));
            return;
        }

        if (req.Path == "/api/sua/areas")
        {
            WriteJson(s, 200, _sua.GetAreas());
            return;
        }

        if (IsMutationPath(req.Path))
        {
            if (!string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(s, 405, new { Error = "POST required." });
                return;
            }
        }

        if (req.Path == "/api/sua/activate")
        {
            req.Query.TryGetValue("name", out var name);
            var minutes = 0;
            if (req.Query.TryGetValue("minutes", out var minStr))
                int.TryParse(minStr, out minutes);
            WriteJson(s, 200, _sua.Activate(name ?? "", minutes));
            return;
        }

        if (req.Path == "/api/sua/deactivate")
        {
            req.Query.TryGetValue("name", out var name);
            WriteJson(s, 200, _sua.Deactivate(name ?? ""));
            return;
        }

        if (req.Path == "/api/sua/deactivateall")
        {
            WriteJson(s, 200, _sua.DeactivateAll());
            return;
        }

        if (req.Path == "/api/sua/windows")
        {
            req.Query.TryGetValue("name", out var name);
            req.Query.TryGetValue("windows", out var spec);
            WriteJson(s, 200, _sua.SetWindows(name ?? "", spec ?? ""));
            return;
        }

        if (req.Path == "/api/sua/levels")
        {
            req.Query.TryGetValue("name", out var name);
            int? floor = null, ceiling = null;
            if (req.Query.TryGetValue("floor", out var fs) && int.TryParse(fs, out var f)) floor = f;
            if (req.Query.TryGetValue("ceiling", out var cs) && int.TryParse(cs, out var c)) ceiling = c;
            WriteJson(s, 200, _sua.SetLevels(name ?? "", floor, ceiling));
            return;
        }

        if (req.Path == "/api/sua/notams")
        {
            WriteJson(s, 200, _notam.GetNotams());
            return;
        }

        if (req.Path == "/api/sua/notams/activate")
        {
            if (!req.Query.TryGetValue("id", out var idStr) || !int.TryParse(idStr, out var id))
            {
                WriteJson(s, 400, new { Error = "id parameter required." });
                return;
            }
            req.Query.TryGetValue("mode", out var mode);
            WriteJson(s, 200, _notam.ActivateNotam(id, mode ?? "now"));
            return;
        }

        WriteJson(s, 404, new { Error = "Not found." });
    }

    private static bool IsMutationPath(string path) =>
        path == "/api/sua/activate" ||
        path == "/api/sua/deactivate" ||
        path == "/api/sua/deactivateall" ||
        path == "/api/sua/windows" ||
        path == "/api/sua/levels" ||
        path == "/api/sua/notams/activate";

    private void WriteJson(NetworkStream s, int code, object payload)
    {
        WriteResponse(s, code, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_json.Serialize(payload)));
    }

    private static void WriteRedirect(NetworkStream s, string location)
    {
        var header =
            "HTTP/1.1 302 Found\r\n" +
            "Location: " + location.Replace("\r", "").Replace("\n", "") + "\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n" +
            "Cache-Control: no-store\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(header);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void WriteResponse(NetworkStream s, int code, string ct, byte[] body)
    {
        var status = code switch
        {
            200 => "OK", 204 => "No Content", 400 => "Bad Request",
            401 => "Unauthorized", 404 => "Not Found", 405 => "Method Not Allowed",
            500 => "Internal Server Error", _ => "OK",
        };

        var header =
            "HTTP/1.1 " + code + " " + status + "\r\n" +
            "Content-Type: " + ct + "\r\n" +
            "Content-Length: " + body.Length + "\r\n" +
            "Connection: close\r\n" +
            "Cache-Control: no-store\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n\r\n";

        var hdr = Encoding.ASCII.GetBytes(header);
        s.Write(hdr, 0, hdr.Length);
        if (body.Length > 0) s.Write(body, 0, body.Length);
    }

    private static HttpReq? ReadRequest(NetworkStream stream)
    {
        var buf = new List<byte>(2048);
        var one = new byte[1];
        var di = 0;

        while (buf.Count < 32768)
        {
            if (stream.Read(one, 0, 1) <= 0) return null;
            buf.Add(one[0]);
            if (one[0] == HeaderEnd[di]) { di++; if (di == HeaderEnd.Length) break; }
            else di = one[0] == HeaderEnd[0] ? 1 : 0;
        }

        var text = Encoding.ASCII.GetString(buf.ToArray(), 0, buf.Count - HeaderEnd.Length);
        var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0) return null;

        var rl = lines[0].Split(' ');
        if (rl.Length < 2) return null;

        var rawPath = rl[1].Trim();
        var qi = rawPath.IndexOf('?');
        var path = qi < 0 ? rawPath : rawPath.Substring(0, qi);
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (qi >= 0)
        {
            foreach (var pair in rawPath.Substring(qi + 1).Split('&'))
            {
                if (string.IsNullOrWhiteSpace(pair)) continue;
                var eq = pair.IndexOf('=');
                if (eq <= 0) { query[Uri.UnescapeDataString(pair)] = ""; continue; }
                query[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
        }

        return new HttpReq { Method = rl[0].Trim(), Path = path, Query = query, Headers = headers };
    }

    private sealed class HttpReq
    {
        public string Method { get; set; } = "";
        public string Path { get; set; } = "";
        public Dictionary<string, string> Query { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
