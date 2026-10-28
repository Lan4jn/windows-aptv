using System.Net;
using System.Net.Sockets;
using System.Text;
using Iptv.Core.Models;
using Iptv.Core.Networking;
using Xunit.Abstractions;

namespace Iptv.Core.Tests.Networking;

public sealed class IndependentProxyAuthenticationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(ProxyKind.Http)]
    [InlineData(ProxyKind.Socks5)]
    public async Task ExplicitBadAuthRejection_PrecedesTargetProtocol_AndReachableOriginIsNotBypassed(ProxyKind kind)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var rejected = 0;
        var challenged = 0;
        var acceptedOrigin = 0;
        await using var origin = new Server(async (stream, ct) =>
        {
            Interlocked.Increment(ref acceptedOrigin);
            await Headers(stream, ct);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\ndirect"), ct);
        });
        await using var proxy = new Server(async (stream, ct) =>
        {
            if (kind == ProxyKind.Socks5)
            {
                var greeting = await Bytes(stream, 2, ct);
                Assert.Equal(5, greeting[0]);
                var methods = await Bytes(stream, greeting[1], ct);
                Assert.Contains((byte)2, methods);
                await stream.WriteAsync(new byte[] { 5, 2 }, ct);
                var auth = await Bytes(stream, 2, ct);
                Assert.Equal(1, auth[0]);
                var username = await Bytes(stream, auth[1], ct);
                var password = await Bytes(stream, (await Bytes(stream, 1, ct))[0], ct);
                Assert.True(username.SequenceEqual(Encoding.UTF8.GetBytes("synthetic-user")));
                Assert.True(password.SequenceEqual(Encoding.UTF8.GetBytes("synthetic-rejected")));
                Interlocked.Increment(ref rejected);
                // Explicit RFC1929 failure occurs before any CONNECT/ATYP parsing.
                await stream.WriteAsync(new byte[] { 1, 1 }, ct);
            }
            else
            {
                var headers = await Headers(stream, ct);
                var expected = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-user:synthetic-rejected"));
                if (headers.Split("\r\n").Any(h => h.Equals(expected, StringComparison.OrdinalIgnoreCase)))
                    Interlocked.Increment(ref rejected);
                else Interlocked.Increment(ref challenged);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=independent\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct);
            }
        });
        var profile = new UserProxy("p", "synthetic", kind, $"{(kind == ProxyKind.Http ? "http" : "socks5")}://127.0.0.1:{proxy.Port}", "synthetic-user", "synthetic-rejected");
        using var lease = ProxyHttpClientFactory.Create(ProxyPolicyResolver.ResolveSource(new SourceConfig("s", "synthetic", SourceKind.M3u, "http://origin.invalid/list", proxyId: "p"), new[] { profile }));
        foreach (var url in new[] { "http://origin.invalid:18473/test", $"http://127.0.0.1:{origin.Port}/reachable" })
        {
            var before = Volatile.Read(ref rejected);
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => lease.Client.GetStringAsync(url, timeout.Token));
            Assert.DoesNotContain("synthetic-rejected", error.ToString());
            Assert.True(Volatile.Read(ref rejected) > before, "Expected observed authentication reject for each target, not protocol mismatch.");
            Assert.Equal(0, Volatile.Read(ref acceptedOrigin));
        }
        using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        Assert.Equal("direct", await direct.GetStringAsync($"http://127.0.0.1:{origin.Port}/reachable", timeout.Token));
        Assert.Equal(1, Volatile.Read(ref acceptedOrigin));
        Assert.Equal(0, proxy.Failures);
        Assert.Equal(0, origin.Failures);
        output.WriteLine($"kind={kind}; explicitAuthRejects={rejected}; httpChallenges={challenged}; originBeforeDirect=0; originAfterExplicitDirect={acceptedOrigin}; protocolFailures={proxy.Failures}");
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _clients = new();
        private readonly Task _loop;
        private int _failures;
        public int Port { get; }
        public int Failures => Volatile.Read(ref _failures);
        public Server(Func<NetworkStream, CancellationToken, Task> handler)
        {
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Loop(handler);
        }
        private async Task Loop(Func<NetworkStream, CancellationToken, Task> handler)
        {
            try { while (!_stop.IsCancellationRequested) { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _clients.Add(Handle(client, handler)); } }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }
        private async Task Handle(TcpClient client, Func<NetworkStream, CancellationToken, Task> handler)
        {
            using (client) try { await handler(client.GetStream(), _stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch { Interlocked.Increment(ref _failures); }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _loop; await Task.WhenAll(_clients); _stop.Dispose(); }
    }
    private static async Task<byte[]> Bytes(Stream stream, int count, CancellationToken ct) { var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, ct); return bytes; }
    private static async Task<string> Headers(Stream stream, CancellationToken ct)
    {
        var data = new List<byte>(); var one = new byte[1];
        while (data.Count < 16384) { if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException(); data.Add(one[0]); if (data.Count >= 4 && data[^4] == 13 && data[^3] == 10 && data[^2] == 13 && data[^1] == 10) return Encoding.ASCII.GetString(data.ToArray()); }
        throw new InvalidDataException("Synthetic header limit.");
    }
}
