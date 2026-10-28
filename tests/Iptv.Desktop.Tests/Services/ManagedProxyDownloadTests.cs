using System.Net;
using System.Net.Http;
using Iptv.Core.Models;
using Iptv.Desktop.Services;

namespace Iptv.Desktop.Tests.Services;

public sealed class ManagedProxyDownloadTests
{
    [Theory]
    [InlineData(ProxyKind.Http, SourceKind.M3u)]
    [InlineData(ProxyKind.Socks5, SourceKind.M3u)]
    [InlineData(ProxyKind.Http, SourceKind.Xtream)]
    [InlineData(ProxyKind.Socks5, SourceKind.Xtream)]
    public async Task ActualPlaylistService_UsesCurrentProxyAndPreservesOriginHeaders(ProxyKind kind, SourceKind sourceKind)
    {
        await using var sockets = new ManagedProxySocketFixture(kind);
        var library = new MemoryOnlyLibraryService();
        var source = new SourceConfig("s", "fixture source", sourceKind, "http://origin.invalid:18473", username: "fixture-user", password: "fixture-pass",
            headers: new Dictionary<string, string> { ["User-Agent"] = "FixturePlayer/1", ["Referer"] = "http://fixture.invalid/" });
        await library.SaveConfigAsync(source);
        await library.SaveUserProxyAsync(sockets.Profile());
        await library.SetSourceProxyAsync("s", "p");
        var policy = new CoreProxyPolicyService(library);
        using var systemClient = new HttpClient(new PoisonHandler());
        var service = new CorePlaylistService(systemClient, proxyPolicyService: policy);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        // Caller holds a stale unassigned source: each actual operation must resolve the current library snapshot.
        var result = await service.LoadChannelsAsync(source, timeout.Token);
        Assert.Single(result.Channels);
        Assert.Equal(sourceKind == SourceKind.M3u ? 1 : 3, sockets.OriginHits);
        Assert.True(sockets.ProxyHits >= sockets.OriginHits);
        Assert.All(sockets.OriginRequests, request =>
        {
            Assert.Contains("User-Agent: FixturePlayer/1", request, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Referer: http://fixture.invalid/", request, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Proxy-Authorization:", request, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData(ProxyKind.Http)]
    [InlineData(ProxyKind.Socks5)]
    public async Task ActualXtreamService_UsesProxyWithoutPlaylistWrapper(ProxyKind kind)
    {
        await using var sockets = new ManagedProxySocketFixture(kind);
        var library = new MemoryOnlyLibraryService();
        var source = new SourceConfig("s", "source", SourceKind.Xtream, "http://origin.invalid:18473", username: "fixture-user", password: "fixture-pass");
        await library.SaveConfigAsync(source); await library.SaveUserProxyAsync(sockets.Profile()); await library.SetSourceProxyAsync("s", "p");
        using var client = new HttpClient(new PoisonHandler());
        var service = new CoreXtreamService(client, new CoreProxyPolicyService(library));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Assert.Single((await service.LoadLiveChannelsWithEpgAsync(source, timeout.Token)).Channels);
        Assert.Equal(3, sockets.ProxyHits);
        Assert.Equal(3, sockets.OriginHits);
    }

    [Theory]
    [InlineData(ProxyKind.Http)]
    [InlineData(ProxyKind.Socks5)]
    public async Task ActualPlaylist_ClosedProxyDoesNotFallBackToReachableOrigin(ProxyKind kind)
    {
        await using var sockets = new ManagedProxySocketFixture(kind);
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var library = new MemoryOnlyLibraryService();
        var source = new SourceConfig("s", "source", SourceKind.M3u, $"http://127.0.0.1:{sockets.OriginPort}/list?token=fixture-secret");
        await library.SaveConfigAsync(source);
        await library.SaveUserProxyAsync(new UserProxy("p", "closed", kind, $"{(kind == ProxyKind.Http ? "http" : "socks5")}://127.0.0.1:{closedPort}"));
        await library.SetSourceProxyAsync("s", "p");
        using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var service = new CorePlaylistService(direct, proxyPolicyService: new CoreProxyPolicyService(library));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadChannelsAsync(source, timeout.Token));
        Assert.DoesNotContain("fixture-secret", error.ToString());
        Assert.DoesNotContain("127.0.0.1", error.ToString());
        Assert.Equal(0, sockets.OriginHits);
    }

    [Theory]
    [InlineData(ProxyKind.Http)]
    [InlineData(ProxyKind.Socks5)]
    public async Task ActualPlaylist_CancellationAfterObservedProxyRequestNeverUsesDirectClient(ProxyKind kind)
    {
        await using var sockets = new ManagedProxySocketFixture(kind) { Hold = true };
        var library = new MemoryOnlyLibraryService();
        var source = new SourceConfig("s", "source", SourceKind.M3u, $"http://127.0.0.1:{sockets.OriginPort}/list");
        await library.SaveConfigAsync(source); await library.SaveUserProxyAsync(sockets.Profile()); await library.SetSourceProxyAsync("s", "p");
        using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var service = new CorePlaylistService(direct, proxyPolicyService: new CoreProxyPolicyService(library));
        using var caller = new CancellationTokenSource();
        var load = service.LoadChannelsAsync(source, caller.Token);
        await sockets.RequestObserved.Task.WaitAsync(TimeSpan.FromSeconds(8)); caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.Equal(0, sockets.OriginHits);
        Assert.True(sockets.ProxyHits > 0);
    }

    [Fact]
    public async Task ActualEntrypoints_RejectProxyAuthorizationAndUnavailableReferencesBeforeAnyRequest()
    {
        await using var sockets = new ManagedProxySocketFixture(ProxyKind.Http);
        using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var dangerous = new SourceConfig("s", "source", SourceKind.M3u, $"http://127.0.0.1:{sockets.OriginPort}/list",
            headers: new Dictionary<string, string> { ["pRoXy-Authorization"] = "fixture-secret" });
        var noContext = new CorePlaylistService(direct);
        var headerError = await Assert.ThrowsAsync<InvalidOperationException>(() => noContext.LoadChannelsAsync(dangerous));
        Assert.DoesNotContain("fixture-secret", headerError.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => noContext.LoadChannelsAsync(dangerous with { Headers = null, ProxyId = "missing" }));
        Assert.Equal(0, sockets.OriginHits);
        Assert.Equal(0, sockets.ProxyHits);
    }

    [Fact]
    public async Task SystemPolicy_BorrowsExternalClientWithoutDisposingIt()
    {
        var handler = new FixedPlaylistHandler();
        using var borrowed = new HttpClient(handler);
        var source = new SourceConfig("s", "source", SourceKind.M3u, "http://fixture.invalid/list");
        Assert.Single((await new CorePlaylistService(borrowed).LoadChannelsAsync(source)).Channels);
        Assert.False(handler.Disposed);
        Assert.NotEmpty(await borrowed.GetStringAsync(source.Location));
    }

    private sealed class FixedPlaylistHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U\n#EXTINF:-1,fixture\nhttp://fixture.invalid/stream\n") });
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class PoisonHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("System client must not receive resolved proxy requests.");
    }
}
