using System.ComponentModel;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Iptv.Core.Models;
using Iptv.Core.Networking;
using Xunit.Abstractions;

namespace Iptv.Core.Tests.Networking;

public sealed class ProxyTlsTests
{
    private readonly ITestOutputHelper? _output;

    public ProxyTlsTests(ITestOutputHelper? output = null)
    {
        _output = output;
    }

    [Theory]
    [InlineData(ProxyKind.Http)]
    [InlineData(ProxyKind.Socks5)]
    public async Task TrustedRootAndMatchingSanReachTlsOriginThroughAuthenticatedProxy(ProxyKind kind)
    {
        using var certificates = new Certificates("origin.invalid");
        await using var fixture = new TunnelFixture(kind, certificates.Leaf, _output);
        using var lease = ProxyHttpClientFactory.Create(Policy(kind, fixture.Port), certificates.TrustPolicy());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var body = await lease.Client.GetStringAsync("https://origin.invalid:18473/synthetic", timeout.Token);
        Assert.Equal("tls-fixture", body);
        Assert.True(fixture.Authenticated);
        Assert.Equal(1, fixture.Tunnels);
        Assert.Equal(1, fixture.TlsRequests);
    }

    [Theory]
    [InlineData(ProxyKind.Http, "name")]
    [InlineData(ProxyKind.Http, "trust")]
    [InlineData(ProxyKind.Socks5, "name")]
    [InlineData(ProxyKind.Socks5, "trust")]
    public async Task WrongSanOrUntrustedRootFailsBeforeTlsHttpRequest(ProxyKind kind, string failure)
    {
        using var certificates = new Certificates(failure == "name" ? "wrong.invalid" : "origin.invalid");
        using var unrelated = new Certificates("origin.invalid");
        await using var fixture = new TunnelFixture(kind, certificates.Leaf, _output);
        var trust = failure == "trust" ? unrelated.TrustPolicy() : certificates.TrustPolicy();
        using var lease = ProxyHttpClientFactory.Create(Policy(kind, fixture.Port), trust);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => lease.Client.GetStringAsync("https://origin.invalid:18473/synthetic", timeout.Token));
        Assert.IsType<AuthenticationException>(error.InnerException);
        Assert.True(fixture.Authenticated);
        Assert.Equal(1, fixture.Tunnels);
        Assert.Equal(1, fixture.TlsConnections);
        Assert.Equal(0, fixture.TlsRequests);
    }

    [Theory]
    [InlineData(ProxyKind.Http)]
    [InlineData(ProxyKind.Socks5)]
    public async Task PublicFactoryDoesNotTrustPrivateFixtureRoot(ProxyKind kind)
    {
        using var certificates = new Certificates("origin.invalid");
        await using var fixture = new TunnelFixture(kind, certificates.Leaf, _output);
        using var lease = ProxyHttpClientFactory.Create(Policy(kind, fixture.Port));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => lease.Client.GetStringAsync("https://origin.invalid:18473/synthetic", timeout.Token));
        Assert.IsType<AuthenticationException>(error.InnerException);
        Assert.Equal(1, fixture.Tunnels);
        Assert.Equal(0, fixture.TlsRequests);
    }

    private static ProxyResolution Policy(ProxyKind kind, int port)
    {
        var profile = new UserProxy("p", "synthetic", kind,
            $"{(kind == ProxyKind.Http ? "http" : "socks5")}://127.0.0.1:{port}", "fixture-user", "fixture-secret");
        var source = new SourceConfig("s", "source", SourceKind.M3u, "http://origin.invalid", proxyId: "p");
        return ProxyPolicyResolver.ResolveSource(source, new[] { profile });
    }

    private sealed class Certificates : IDisposable
    {
        private readonly RSA _rootKey = RSA.Create(2048);
        private readonly RSA _leafKey = RSA.Create(2048);
        public X509Certificate2 Root { get; }
        public X509Certificate2 Leaf { get; }

        public Certificates(string name)
        {
            var now = DateTimeOffset.UtcNow;
            var rootRequest = new CertificateRequest("CN=Synthetic Test Root", _rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            Root = rootRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(2));
            var leafRequest = new CertificateRequest("CN=Synthetic Test Server", _leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(name);
            leafRequest.CertificateExtensions.Add(san.Build());
            leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            using var issued = leafRequest.Create(Root, now.AddMinutes(-2), now.AddDays(1), RandomNumberGenerator.GetBytes(16));
            if (OperatingSystem.IsWindows())
            {
                using var withKey = issued.CopyWithPrivateKey(_leafKey);
                var pfx = withKey.Export(X509ContentType.Pfx);
                try
                {
                    Leaf = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pfx);
                }
            }
            else
            {
                Leaf = issued.CopyWithPrivateKey(_leafKey);
            }
        }

        public X509ChainPolicy TrustPolicy()
        {
            var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            policy.CustomTrustStore.Add(Root);
            policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            return policy;
        }

        public void Dispose() { Leaf.Dispose(); Root.Dispose(); _leafKey.Dispose(); _rootKey.Dispose(); }
    }

    private sealed class TunnelFixture : IAsyncDisposable
    {
        private readonly Listener _origin;
        private readonly Listener _proxy;
        private readonly ITestOutputHelper? _output;
        private readonly List<string> _serverDiagnostics = new();
        private readonly object _diagnosticLock = new();
        private int _tunnels;
        private int _tlsRequests;
        private int _tlsConnections;
        private int _authenticated;
        public int Port => _proxy.Port;
        public int Tunnels => Volatile.Read(ref _tunnels);
        public int TlsRequests => Volatile.Read(ref _tlsRequests);
        public int TlsConnections => Volatile.Read(ref _tlsConnections);
        public bool Authenticated => Volatile.Read(ref _authenticated) != 0;
        public IReadOnlyList<string> ServerDiagnostics { get { lock (_diagnosticLock) return _serverDiagnostics.ToArray(); } }

        public TunnelFixture(ProxyKind kind, X509Certificate2 certificate, ITestOutputHelper? output = null)
        {
            _output = output;
            _origin = new Listener(async (stream, ct) =>
            {
                Interlocked.Increment(ref _tlsConnections);
                try
                {
                    using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                    }, ct);
                    await ReadHeaders(tls, ct);
                    Interlocked.Increment(ref _tlsRequests);
                    await Write(tls, "HTTP/1.1 200 OK\r\nContent-Length: 11\r\nConnection: close\r\n\r\ntls-fixture", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RecordServerException(ex);
                    throw;
                }
            });
            _proxy = new Listener(async (stream, ct) =>
            {
                if (kind == ProxyKind.Http)
                {
                    var headers = await ReadHeaders(stream, ct);
                    if (!headers.StartsWith("CONNECT origin.invalid:18473 HTTP/1.1\r\n", StringComparison.Ordinal))
                        throw new InvalidDataException("Fixture requires the expected CONNECT target.");
                    var expected = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("fixture-user:fixture-secret"));
                    if (!headers.Split("\r\n").Any(line => line.Equals(expected, StringComparison.OrdinalIgnoreCase)))
                    {
                        await Write(stream, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=fixture\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);
                        return;
                    }
                    await Write(stream, "HTTP/1.1 200 Connection Established\r\n\r\n", ct);
                }
                else
                {
                    var greeting = await Bytes(stream, 2, ct);
                    var methods = await Bytes(stream, greeting[1], ct);
                    if (greeting[0] != 5 || !methods.Contains((byte)2)) throw new InvalidDataException("Fixture requires SOCKS5 auth.");
                    await stream.WriteAsync(new byte[] { 5, 2 }, ct);
                    var auth = await Bytes(stream, 2, ct);
                    var username = await Bytes(stream, auth[1], ct);
                    var length = (await Bytes(stream, 1, ct))[0];
                    var password = await Bytes(stream, length, ct);
                    if (!username.SequenceEqual(Encoding.ASCII.GetBytes("fixture-user")) || !password.SequenceEqual(Encoding.ASCII.GetBytes("fixture-secret")))
                        throw new InvalidDataException("Fixture auth mismatch.");
                    await stream.WriteAsync(new byte[] { 1, 0 }, ct);
                    var command = await Bytes(stream, 4, ct);
                    if (command[1] != 1 || command[3] != 3) throw new InvalidDataException("Fixture requires domain CONNECT.");
                    var hostLength = (await Bytes(stream, 1, ct))[0];
                    var host = await Bytes(stream, hostLength, ct);
                    var port = await Bytes(stream, 2, ct);
                    if (Encoding.ASCII.GetString(host) != "origin.invalid" || ((port[0] << 8) | port[1]) != 18473)
                        throw new InvalidDataException("Fixture target mismatch.");
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, ct);
                }
                Interlocked.Exchange(ref _authenticated, 1);
                Interlocked.Increment(ref _tunnels);
                using var origin = new TcpClient();
                await origin.ConnectAsync(IPAddress.Loopback, _origin.Port, ct);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var upstream = stream.CopyToAsync(origin.GetStream(), linked.Token);
                var downstream = origin.GetStream().CopyToAsync(stream, linked.Token);
                await Task.WhenAny(upstream, downstream);
                linked.Cancel();
                try { await Task.WhenAll(upstream, downstream); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            });
        }

        private void RecordServerException(Exception ex)
        {
            var sb = new StringBuilder();
            sb.Append($"Server TLS exception: {ex.GetType().FullName}: {ex.Message} (HResult: 0x{ex.HResult:X8})");
            if (ex.InnerException != null)
            {
                sb.Append($" ---> {ex.InnerException.GetType().FullName}: {ex.InnerException.Message} (HResult: 0x{ex.InnerException.HResult:X8})");
                if (ex.InnerException is Win32Exception win32)
                {
                    sb.Append($" [NativeErrorCode: 0x{win32.NativeErrorCode:X8}]");
                }
            }
            var line = sb.ToString();
            lock (_diagnosticLock)
            {
                _serverDiagnostics.Add(line);
            }
            _output?.WriteLine($"[ProxyTls Server Diagnostic] {line}");
        }

        public async ValueTask DisposeAsync()
        {
            await _proxy.DisposeAsync();
            await _origin.DisposeAsync();
            lock (_diagnosticLock)
            {
                if (_serverDiagnostics.Count > 0)
                {
                    _output?.WriteLine($"[ProxyTls Fixture Summary] Server recorded {_serverDiagnostics.Count} exception(s):");
                    foreach (var diag in _serverDiagnostics)
                    {
                        _output?.WriteLine($"  {diag}");
                    }
                }
            }
        }
    }

    private sealed class Listener : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _clients = new();
        private readonly Task _loop;
        public int Port { get; }

        public Listener(Func<NetworkStream, CancellationToken, Task> serve)
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
                catch (IOException) { }
                catch (AuthenticationException) { }
                catch (CryptographicException) { }
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

    private static async Task<byte[]> Bytes(Stream stream, int count, CancellationToken ct)
    {
        var result = new byte[count];
        await stream.ReadExactlyAsync(result, ct);
        return result;
    }

    private static async Task<string> ReadHeaders(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        while (bytes.Count < 16384)
        {
            bytes.Add((await Bytes(stream, 1, ct))[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("Fixture header limit.");
    }

    private static Task Write(Stream stream, string text, CancellationToken ct) => stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();
}
