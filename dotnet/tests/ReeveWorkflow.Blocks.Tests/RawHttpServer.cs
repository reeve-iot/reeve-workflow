using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ReeveWorkflow.Blocks.Tests;

internal sealed class RawHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<int, string> _extraResponseHeaders;
    private readonly List<string> _requests = [];

    public RawHttpServer(Func<int, string>? extraResponseHeaders = null)
    {
        _extraResponseHeaders = extraResponseHeaders ?? (_ => "");
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

    public IReadOnlyList<string> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var received = new List<byte>();
            var buffer = new byte[4096];
            var headEnd = -1;
            while (headEnd < 0)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) return;
                received.AddRange(buffer.AsSpan(0, read).ToArray());
                headEnd = Find(received, "\r\n\r\n"u8.ToArray());
            }

            var head = Encoding.Latin1.GetString(received.GetRange(0, headEnd).ToArray());
            var length = 0;
            foreach (var line in head.Split("\r\n"))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line[15..].Trim());
            while (received.Count < headEnd + 4 + length)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) break;
                received.AddRange(buffer.AsSpan(0, read).ToArray());
            }

            int number;
            lock (_requests)
            {
                _requests.Add(Encoding.Latin1.GetString(received.ToArray()));
                number = _requests.Count;
            }

            var response = $"HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n{_extraResponseHeaders(number)}\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response)).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    private static int Find(List<byte> haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Count; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length && match; j++)
                match = haystack[i + j] == needle[j];
            if (match) return i;
        }

        return -1;
    }

    public void Dispose() => _listener.Stop();
}
