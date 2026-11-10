using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PatchTool;

/// <summary>
/// Minimal read-only HTTP/1.1 file server for local testing (GET/HEAD only, localhost by default).
/// Not meant for the internet: host real patches with a proper web server (see docs/hosting.md).
/// </summary>
internal static class StaticServer
{
    public static async Task RunAsync(string root, IPAddress bind, int port, CancellationToken ct)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var listener = new TcpListener(bind, port);
        listener.Start();
        Console.WriteLine($"Serving {root} on http://{bind}:{port}/ (Ctrl+C to stop)");
        using var reg = ct.Register(listener.Stop);
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            _ = Task.Run(() => Handle(client, root, ct));
        }
    }

    private static async Task Handle(TcpClient client, string root, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            while (true)
            {
                var requestLine = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(requestLine)) return;
                string? line;
                bool close = false;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct)))
                    if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) && line.Contains("close", StringComparison.OrdinalIgnoreCase)) close = true;

                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || (parts[0] != "GET" && parts[0] != "HEAD"))
                {
                    await Write(stream, 405, "text/plain", Encoding.ASCII.GetBytes("method not allowed"), true, ct);
                    return;
                }
                var path = Uri.UnescapeDataString(parts[1].Split('?')[0]).TrimStart('/');
                var full = Path.GetFullPath(Path.Combine(root, path));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                {
                    await Write(stream, 404, "text/plain", Encoding.ASCII.GetBytes("not found"), parts[0] == "GET", ct);
                    if (close) return;
                    continue;
                }
                var len = new FileInfo(full).Length;
                var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {len}\r\nCache-Control: no-cache\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
                if (parts[0] == "GET")
                {
                    await using var fs = File.OpenRead(full);
                    await fs.CopyToAsync(stream, ct);
                }
                if (close) return;
            }
        }
        catch (Exception) { /* client went away */ }
    }

    private static async Task Write(NetworkStream s, int code, string type, byte[] body, bool withBody, CancellationToken ct)
    {
        var reason = code switch { 404 => "Not Found", 405 => "Method Not Allowed", _ => "OK" };
        var header = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        if (withBody) await s.WriteAsync(body, ct);
    }
}
