using System.Net;
using System.Net.Sockets;
using System.Text;
using Iptv.Core.Models;
using Iptv.Core.Networking;

namespace Iptv.Core.Tests.Networking;

public sealed class ProxyTransportTests
{
    [Theory]
    [InlineData(ProxyKind.Http, false)]
    [InlineData(ProxyKind.Http, true)]
    [InlineData(ProxyKind.Socks5, false)]
    [InlineData(ProxyKind.Socks5, true)]
    public async Task ActualProxyRoutesInvalidHostToLoopbackOrigin(ProxyKind kind, bool auth)
    {
        await using var origin = new SocketServer(async (stream, ct) =>
        {
            var request = await ReadHeaders(stream, ct);
            Assert.False(request.Contains("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
            await Write(stream, "HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\nfixture", ct);
        });
        await using var proxy = new ProxyFixture(kind, auth, origin.Port);
        using var lease = Create(kind, proxy.Port, auth);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var body = await lease.Client.GetStringAsync("http://origin.invalid:18473/synthetic", timeout.Token);
        Assert.Equal("fixture", body);
        Assert.True(proxy.TargetMatched);
        Assert.Equal(auth, proxy.Authenticated);
        Assert.Equal(1, origin.Accepted);
        Assert.Equal(0, proxy.Server.Failures);
        Assert.Equal(0, origin.Failures);
    }

    [Theory]
    [InlineData(ProxyKind.Http)]
    [InlineData(ProxyKind.Socks5)]
    public async Task WrongAuthenticationNeverReachesOrigin(ProxyKind kind)
    {
        await using var origin = new SocketServer((_, _) => Task.CompletedTask);
        await using var proxy = new ProxyFixture(kind, true, origin.Port);
        using var lease = Create(kind, proxy.Port, auth: true, password: "synthetic-wrong");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => lease.Client.GetStringAsync("http://origin.invalid:18473/synthetic", timeout.Token));
        Assert.False(ex.ToString().Contains("synthetic-wrong", StringComparison.Ordinal));
        Assert.False(proxy.Authenticated);
        // A reachable origin would expose a silent retry or local-address bypass.
        await Assert.ThrowsAsync<HttpRequestException>(() => lease.Client.GetStringAsync($"http://127.0.0.1:{origin.Port}/synthetic", timeout.Token));
        Assert.Equal(0, origin.Accepted);
        Assert.True(proxy.Server.Accepted > 0);
    }

    [Theory]
    [InlineData(ProxyKind.Http)]
    [InlineData(ProxyKind.Socks5)]
    public async Task ClosedProxyDoesNotFallBackToOrigin(ProxyKind kind)
    {
        await using var origin = new SocketServer((_, _) => Task.CompletedTask);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var lease = Create(kind, port, false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAsync<HttpRequestException>(() => lease.Client.GetStringAsync("http://origin.invalid:18473/synthetic", timeout.Token));
        await Assert.ThrowsAsync<HttpRequestException>(() => lease.Client.GetStringAsync($"http://127.0.0.1:{origin.Port}/synthetic", timeout.Token));
        Assert.Equal(0, origin.Accepted);
    }

    [Theory]
    [InlineData(ProxyKind.Http)]
    [InlineData(ProxyKind.Socks5)]
    public async Task CallerCancellationStopsAnObservedProxyRequest(ProxyKind kind)
    {
        await using var origin = new SocketServer((_, _) => Task.CompletedTask);
        await using var proxy = new ProxyFixture(kind, false, origin.Port, hold: true);
        using var lease = Create(kind, proxy.Port, false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var caller = new CancellationTokenSource();
        var request = lease.Client.GetStringAsync("http://origin.invalid:18473/synthetic", caller.Token);
        await proxy.RequestObserved.Task.WaitAsync(timeout.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(0, origin.Accepted);
    }

    private static ProxyHttpClientLease Create(ProxyKind kind, int port, bool auth, string password = "synthetic-secret")
    {
        var profile = new UserProxy("p", "synthetic", kind, $"{(kind == ProxyKind.Http ? "http" : "socks5")}://127.0.0.1:{port}",
            auth ? "synthetic-user" : null, auth ? password : null);
        var source = new SourceConfig("s", "source", SourceKind.M3u, "http://origin.invalid/list", proxyId: "p");
        return ProxyHttpClientFactory.Create(ProxyPolicyResolver.ResolveSource(source, new[] { profile }));
    }

    private sealed class ProxyFixture : IAsyncDisposable
    {
        public SocketServer Server { get; }
        public int Port => Server.Port;
        public bool TargetMatched { get; private set; }
        public bool Authenticated { get; private set; }
        public TaskCompletionSource RequestObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProxyFixture(ProxyKind kind, bool auth, int originPort, bool hold = false)
        {
            Server = new SocketServer(async (stream, ct) =>
            {
                if (kind == ProxyKind.Socks5)
                {
                    var greeting = await ReadBytes(stream, 2, ct);
                    if (greeting[0] != 5) throw new InvalidDataException("Fixture protocol mismatch.");
                    var methods = await ReadBytes(stream, greeting[1], ct);
                    var method = (byte)(auth ? 2 : 0);
                    if (!methods.Contains(method))
                    {
                        await stream.WriteAsync(new byte[] { 5, 255 }, ct);
                        return;
                    }
                    await stream.WriteAsync(new byte[] { 5, method }, ct);
                    if (auth)
                    {
                        var credentials = await ReadBytes(stream, 2, ct);
                        var username = await ReadBytes(stream, credentials[1], ct);
                        var length = (await ReadBytes(stream, 1, ct))[0];
                        var password = await ReadBytes(stream, length, ct);
                        var correct = username.SequenceEqual(Encoding.UTF8.GetBytes("synthetic-user"))
                            && password.SequenceEqual(Encoding.UTF8.GetBytes("synthetic-secret"));
                        await stream.WriteAsync(new byte[] { 1, (byte)(correct ? 0 : 1) }, ct);
                        if (!correct) return;
                        Authenticated = true;
                    }
                    var command = await ReadBytes(stream, 4, ct);
                    if (command[0] != 5 || command[1] != 1 || command[3] != 3)
                        throw new InvalidDataException("Fixture expected domain CONNECT.");
                    var hostLength = (await ReadBytes(stream, 1, ct))[0];
                    var host = await ReadBytes(stream, hostLength, ct);
                    var port = await ReadBytes(stream, 2, ct);
                    TargetMatched = Encoding.ASCII.GetString(host) == "origin.invalid" && ((port[0] << 8) | port[1]) == 18473;
                    if (!TargetMatched) throw new InvalidDataException("Fixture target mismatch.");
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, ct);
                }

                var request = await ReadHeaders(stream, ct);
                if (kind == ProxyKind.Http)
                {
                    TargetMatched = request.StartsWith("GET http://origin.invalid:18473/synthetic HTTP/1.1\r\n", StringComparison.Ordinal);
                    var expected = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-user:synthetic-secret"));
                    if (auth && !request.Split("\r\n").Any(s => s.Equals(expected, StringComparison.OrdinalIgnoreCase)))
                    {
                        await Write(stream, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=fixture\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);
                        return;
                    }
                    Authenticated = auth;
                }
                RequestObserved.TrySetResult();
                if (hold)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return;
                }
                using var origin = new TcpClient();
                await origin.ConnectAsync(IPAddress.Loopback, originPort, ct);
                var originStream = origin.GetStream();
                var headers = request.Split("\r\n").Skip(1).Where(s => !s.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
                await Write(originStream, "GET /synthetic HTTP/1.1\r\n" + string.Join("\r\n", headers), ct);
                await originStream.CopyToAsync(stream, ct);
            });
        }

        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }

    private sealed class SocketServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly List<Task> _clients = new();
        private int _accepted;
        private int _failures;
        public int Port { get; }
        public int Accepted => Volatile.Read(ref _accepted);
        public int Failures => Volatile.Read(ref _failures);

        public SocketServer(Func<NetworkStream, CancellationToken, Task> serve)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
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
                catch { Interlocked.Increment(ref _failures); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _loop;
            await Task.WhenAll(_clients);
            _stop.Dispose();
        }
    }

    private static async Task<string> ReadHeaders(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16384)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException();
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("Fixture header limit exceeded.");
    }

    private static async Task<byte[]> ReadBytes(Stream stream, int count, CancellationToken ct)
    {
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    private static Task Write(Stream stream, string value, CancellationToken ct) => stream.WriteAsync(Encoding.ASCII.GetBytes(value), ct).AsTask();
}
