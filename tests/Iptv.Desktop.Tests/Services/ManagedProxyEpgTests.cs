using System.Net.Http;
using Iptv.Core.Models;
using Iptv.Desktop.Services;

namespace Iptv.Desktop.Tests.Services;

public sealed class ManagedProxyEpgTests
{
    private static readonly DateTimeOffset GuideTime = DateTimeOffset.Parse("2026-04-01T00:30:00Z");
    private static readonly Channel Channel = new("c", "s", "fixture channel", "http://stream.invalid/fixture", tvgId: "c1");

    [Theory]
    [InlineData(ProxyKind.Http)]
    [InlineData(ProxyKind.Socks5)]
    public async Task ActualEpg_ContextProxyCacheAndProfileChangesStayIsolated(ProxyKind kind)
    {
        await using var first = new ManagedProxySocketFixture(kind) { ProgrammeTitle = "first" };
        await using var second = new ManagedProxySocketFixture(kind) { ProgrammeTitle = "second" };
        var library = new MemoryOnlyLibraryService();
        var source = new SourceConfig("s", "source", SourceKind.M3u, "http://origin.invalid:18473/list");
        await library.SaveConfigAsync(source); await library.SaveUserProxyAsync(first.Profile()); await library.SetSourceProxyAsync("s", "p");
        using var system = new HttpClient(new PoisonHandler());
        var service = new CoreEpgService(system, new CoreProxyPolicyService(library));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var urls = new[] { "http://origin.invalid:18473/xmltv" };
        await service.EnsureGuideForSourceAsync(source, urls, ct: timeout.Token);
        Assert.Equal("first", (await service.GetNowNextAsync(Channel, GuideTime)).Now?.Title);
        await service.EnsureGuideForSourceAsync(source, urls, ct: timeout.Token);
        Assert.Equal(1, first.OriginHits);
        await library.SaveUserProxyAsync(second.Profile());
        await service.EnsureGuideForSourceAsync(source, urls, ct: timeout.Token);
        Assert.Equal("second", (await service.GetNowNextAsync(Channel, GuideTime)).Now?.Title);
        Assert.Equal(1, second.OriginHits);
        second.ProgrammeTitle = "refreshed";
        await service.EnsureGuideForSourceAsync(source, urls, ct: timeout.Token, forceRefresh: true);
        Assert.Equal("refreshed", (await service.GetNowNextAsync(Channel, GuideTime)).Now?.Title);
        Assert.Equal(2, second.OriginHits);
        await library.DeleteConfigAsync("s");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureGuideForSourceAsync(source, urls));
        Assert.Null((await service.GetNowNextAsync(Channel, GuideTime)).Now);
        Assert.Equal(1, first.OriginHits);
        Assert.Equal(2, second.OriginHits);
    }

    [Fact]
    public async Task ActualEpg_CurrentCustomUrlAndGlobalFallbackUseSourceProxyAndHeaders()
    {
        await using var sockets = new ManagedProxySocketFixture(ProxyKind.Http);
        var library = new MemoryOnlyLibraryService();
        var source = new SourceConfig("s", "source", SourceKind.M3u, "http://origin.invalid:18473/list",
            headers: new Dictionary<string, string> { ["User-Agent"] = "GuideFixture/1", ["Referer"] = "http://guide-fixture.invalid/" });
        await library.SaveConfigAsync(source); await library.SaveUserProxyAsync(sockets.Profile()); await library.SetSourceProxyAsync("s", "p");
        await library.SaveConfigAsync(source with { CustomEpgUrl = "http://origin.invalid:18473/xmltv-custom" });
        using var client = new HttpClient(new PoisonHandler());
        var service = new CoreEpgService(client, new CoreProxyPolicyService(library));
        await service.EnsureGuideForSourceAsync(source, new[] { "http://origin.invalid:18473/xmltv-derived" }, "http://origin.invalid:18473/xmltv-global");
        Assert.Contains("/xmltv-custom", Assert.Single(sockets.OriginRequests));
        await library.SaveConfigAsync(source);
        await service.EnsureGuideForSourceAsync(source, Array.Empty<string>(), "http://origin.invalid:18473/xmltv-global");
        Assert.Contains("/xmltv-global", sockets.OriginRequests.Last());
        Assert.All(sockets.OriginRequests, request =>
        {
            Assert.Contains("User-Agent: GuideFixture/1", request, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Referer: http://guide-fixture.invalid/", request, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Proxy-Authorization:", request, StringComparison.OrdinalIgnoreCase);
        });
        await service.EnsureGuideForSourceAsync(source, Array.Empty<string>());
        Assert.Null((await service.GetNowNextAsync(Channel, GuideTime)).Now);
        Assert.Equal(2, sockets.OriginHits);
    }

    [Fact]
    public async Task ActualEpg_ProfileAuthChangeInvalidatesCache_AndEditedLocationRejectsOldDerivedUrls()
    {
        await using var sockets = new ManagedProxySocketFixture(ProxyKind.Http) { ProgrammeTitle = "before" };
        var library = new MemoryOnlyLibraryService();
        var source = new SourceConfig("s", "source", SourceKind.M3u, "http://origin.invalid:18473/list");
        await library.SaveConfigAsync(source); await library.SaveUserProxyAsync(sockets.Profile()); await library.SetSourceProxyAsync("s", "p");
        var policy = new CoreProxyPolicyService(library);
        using var client = new HttpClient(new PoisonHandler());
        var service = new CoreEpgService(client, policy);
        var urls = new[] { "http://origin.invalid:18473/xmltv" };
        await service.EnsureGuideForSourceAsync(source, urls);
        var oldIdentity = (await policy.ResolveSourceAsync(source)).CacheIdentity;
        sockets.ProgrammeTitle = "after";
        await library.SaveUserProxyAsync(sockets.Profile() with { Username = "fixture-new-user", Password = "fixture-new-password" });
        var identity = (await policy.ResolveSourceAsync(source)).CacheIdentity;
        Assert.NotEqual(oldIdentity, identity);
        Assert.DoesNotContain("fixture-new-password", identity);
        await service.EnsureGuideForSourceAsync(source, urls);
        Assert.Equal("after", (await service.GetNowNextAsync(Channel, GuideTime)).Now?.Title);
        Assert.Equal(2, sockets.OriginHits);
        await library.SaveConfigAsync(source with { Location = "http://origin.invalid:18473/edited-list" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureGuideForSourceAsync(source, urls));
        Assert.Null((await service.GetNowNextAsync(Channel, GuideTime)).Now);
        Assert.Equal(2, sockets.OriginHits);
    }

    [Theory]
    [InlineData(ProxyKind.Http)]
    [InlineData(ProxyKind.Socks5)]
    public async Task ActualEpg_CancelledObservedProxyRequestCannotRepublishAfterClear(ProxyKind kind)
    {
        await using var sockets = new ManagedProxySocketFixture(kind) { Hold = true };
        var library = new MemoryOnlyLibraryService();
        var source = new SourceConfig("s", "source", SourceKind.M3u, "http://origin.invalid:18473/list");
        await library.SaveConfigAsync(source); await library.SaveUserProxyAsync(sockets.Profile()); await library.SetSourceProxyAsync("s", "p");
        using var client = new HttpClient(new PoisonHandler());
        var service = new CoreEpgService(client, new CoreProxyPolicyService(library));
        using var caller = new CancellationTokenSource();
        var load = service.EnsureGuideForSourceAsync(source, new[] { "http://origin.invalid:18473/xmltv" }, ct: caller.Token);
        await sockets.RequestObserved.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await service.EnsureGuideLoadedAsync(null); caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.Null((await service.GetNowNextAsync(Channel, GuideTime)).Now);
        Assert.Equal(0, sockets.OriginHits);
    }

    [Fact]
    public async Task ActualEpg_InvalidProxyReferenceFailsBeforePreviouslyCachedGuide()
    {
        await using var sockets = new ManagedProxySocketFixture(ProxyKind.Http);
        var source = new SourceConfig("s", "source", SourceKind.M3u, "http://origin.invalid:18473/list");
        var library = new FakeLibraryService(configs: new[] { source });
        await library.SaveUserProxyAsync(sockets.Profile()); await library.SetSourceProxyAsync("s", "p");
        using var client = new HttpClient(new PoisonHandler());
        var service = new CoreEpgService(client, new CoreProxyPolicyService(library));
        var urls = new[] { "http://origin.invalid:18473/xmltv" };
        await service.EnsureGuideForSourceAsync(source, urls);
        await library.DeleteConfigAsync("s");
        await library.SaveConfigAsync(source with { ProxyId = "missing" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureGuideForSourceAsync(source, urls));
        Assert.Null((await service.GetNowNextAsync(Channel, GuideTime)).Now);
        Assert.Equal(1, sockets.OriginHits);
    }

    private sealed class PoisonHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("System client must not receive proxied guide requests.");
    }
}
