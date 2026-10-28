using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Iptv.Core.Models;

namespace Iptv.Desktop.Tests.Services;

internal sealed class ManagedProxySocketFixture : IAsyncDisposable
{
    private readonly SocketServer _origin;
    private readonly SocketServer _proxy;
    public int ProxyPort => _proxy.Port;
    public int OriginPort => _origin.Port;
    public int ProxyHits => _proxy.Accepted;
    public int OriginHits => _origin.Accepted;
    public ConcurrentQueue<string> OriginRequests { get; } = new();
    public TaskCompletionSource RequestObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Hold { get; set; }
    public string ProgrammeTitle { get; set; } = "fixture programme";
    public ProxyKind Kind { get; }

    public ManagedProxySocketFixture(ProxyKind kind)
    {
        Kind = kind;
        _origin = new SocketServer(async (stream, ct) =>
        {
            var headers = await ReadHeaders(stream, ct);
            OriginRequests.Enqueue(headers);
            var requestLine = headers.Split("\r\n")[0];
            var body = requestLine.Contains("action=get_live_categories", StringComparison.Ordinal) ? "[{\"category_id\":\"1\",\"category_name\":\"fixture\"}]"
                : requestLine.Contains("action=get_live_streams", StringComparison.Ordinal) ? "[{\"stream_id\":1,\"name\":\"fixture channel\",\"category_id\":\"1\",\"epg_channel_id\":\"c1\"}]"
                : requestLine.Contains("player_api.php", StringComparison.Ordinal) ? "{\"user_info\":{\"auth\":1,\"status\":\"Active\"}}"
                : requestLine.Contains("xmltv", StringComparison.Ordinal) ? $"<tv><channel id='c1'><display-name>fixture channel</display-name></channel><programme channel='c1' start='20260401000000 Z' stop='20260401010000 Z'><title>{ProgrammeTitle}</title></programme></tv>"
                : "#EXTM3U x-tvg-url=\"http://origin.invalid:18473/xmltv\"\n#EXTINF:-1 tvg-id=\"c1\",fixture channel\nhttp://stream.invalid/fixture\n";
            var bytes = Encoding.UTF8.GetBytes(body);
            await Write(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n", ct);
            await stream.WriteAsync(bytes, ct);
        });
        _proxy = new SocketServer(async (stream, ct) =>
        {
            if (kind == ProxyKind.Socks5)
            {
                var greeting = await ReadBytes(stream, 2, ct);
                var methods = await ReadBytes(stream, greeting[1], ct);
                if (greeting[0] != 5 || !methods.Contains((byte)0)) throw new InvalidDataException("SOCKS fixture protocol mismatch.");
                await stream.WriteAsync(new byte[] { 5, 0 }, ct);
                var command = await ReadBytes(stream, 4, ct);
                if (command[3] == 3) await ReadBytes(stream, (await ReadBytes(stream, 1, ct))[0], ct);
                else if (command[3] == 1) await ReadBytes(stream, 4, ct);
                else if (command[3] == 4) await ReadBytes(stream, 16, ct);
                else throw new InvalidDataException("SOCKS fixture address mismatch.");
                await ReadBytes(stream, 2, ct);
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, ct);
            }
            var headers = await ReadHeaders(stream, ct);
            RequestObserved.TrySetResult();
            if (Hold) { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return; }
            var lines = headers.Split("\r\n");
            var first = lines[0].Split(' ');
            var target = Uri.TryCreate(first[1], UriKind.Absolute, out var uri) ? uri.PathAndQuery : first[1];
            var rest = lines.Skip(1).Where(line => !line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
            using var origin = new TcpClient();
            await origin.ConnectAsync(IPAddress.Loopback, _origin.Port, ct);
            await Write(origin.GetStream(), $"{first[0]} {target} HTTP/1.1\r\n" + string.Join("\r\n", rest), ct);
            await origin.GetStream().CopyToAsync(stream, ct);
        });
    }

    public UserProxy Profile(string id = "p") => new(id, "fixture proxy", Kind, $"{(Kind == ProxyKind.Http ? "http" : "socks5")}://127.0.0.1:{ProxyPort}");
    public async ValueTask DisposeAsync() { await _proxy.DisposeAsync(); await _origin.DisposeAsync(); }

    private sealed class SocketServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _clients = new();
        private readonly Task _loop;
        private readonly ConcurrentQueue<Exception> _failures = new();
        private int _accepted;
        public int Port { get; }
        public int Accepted => Volatile.Read(ref _accepted);
        public SocketServer(Func<NetworkStream, CancellationToken, Task> serve)
        {
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Accept(serve);
        }
        private async Task Accept(Func<NetworkStream, CancellationToken, Task> serve)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _accepted);
                    _clients.Add(Handle(client, serve));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }
        private async Task Handle(TcpClient client, Func<NetworkStream, CancellationToken, Task> serve)
        {
            using (client)
            {
                try { await serve(client.GetStream(), _stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (IOException) when (_stop.IsCancellationRequested) { }
                catch (Exception ex) { _failures.Enqueue(ex); }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop(); await _loop; await Task.WhenAll(_clients); _stop.Dispose();
            if (!_failures.IsEmpty) throw new AggregateException(_failures);
        }
    }

    private static async Task<byte[]> ReadBytes(Stream stream, int count, CancellationToken ct)
    { var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, ct); return bytes; }
    private static async Task<string> ReadHeaders(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(); var one = new byte[1];
        while (bytes.Count < 32768)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException();
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("Fixture headers exceeded limit.");
    }
    private static Task Write(Stream stream, string text, CancellationToken ct) => stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();
}
