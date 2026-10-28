using System.Net;
using System.Text.Json;
using Iptv.Core.Models;
using Iptv.Core.Networking;

namespace Iptv.Core.Tests.Networking;

public sealed class ProxyPolicyResolverTests
{
    private static SourceConfig Source(string? id = null) => new("s", "source", SourceKind.M3u, "http://origin.invalid/list", proxyId: id);
    private static UserProxy Profile(string id = "a") => new(id, "profile", ProxyKind.Http, "http://127.0.0.1:19876", "fixture-user", "fixture-secret");
    private static Channel Channel(string? id = null) => new("c", "s", "channel", "http://origin.invalid/live", proxyId: id);

    [Fact]
    public void NoReferencePreservesSystemWithoutResolvingUnrelatedProfiles()
    {
        Assert.Equal(ProxyResolutionStatus.System, ProxyPolicyResolver.ResolveSource(Source(), new[] { Profile(), Profile() }).Status);
        Assert.Equal(ProxyResolutionStatus.System, ProxyPolicyResolver.ResolveChannel(Channel(), new[] { Source() }, Array.Empty<UserProxy>()).Status);
    }

    [Fact]
    public void OverrideWinsAndNullInheritsCurrentSource()
    {
        var profiles = new[] { Profile(), Profile("b") };
        Assert.Equal("b", ProxyPolicyResolver.ResolveChannel(Channel("b"), new[] { Source("a") }, profiles).Snapshot!.Id);
        Assert.Equal("a", ProxyPolicyResolver.ResolveChannel(Channel(), new[] { Source("a") }, profiles).Snapshot!.Id);
        Assert.Equal("b", ProxyPolicyResolver.ResolveChannel(Channel("b"), Array.Empty<SourceConfig>(), profiles).Snapshot!.Id);
    }

    [Theory]
    [InlineData("", ProxyPolicyError.ReferenceInvalid)]
    [InlineData(" ", ProxyPolicyError.ReferenceInvalid)]
    [InlineData("missing", ProxyPolicyError.ReferenceMissing)]
    public void InvalidReferenceNeverFallsBack(string id, ProxyPolicyError error)
    {
        var profiles = new[] { Profile() };
        Assert.Equal(error, ProxyPolicyResolver.ResolveSource(Source(id), profiles).Error);
        Assert.Equal(error, ProxyPolicyResolver.ResolveChannel(Channel(id), new[] { Source("a") }, profiles).Error);
    }

    [Fact]
    public void AmbiguousProfilesAndMissingOrAmbiguousSourceFailClosed()
    {
        Assert.Equal(ProxyPolicyError.ReferenceAmbiguous, ProxyPolicyResolver.ResolveSource(Source("a"), new[] { Profile(), Profile() }).Error);
        Assert.Equal(ProxyPolicyError.SourceMissing, ProxyPolicyResolver.ResolveChannel(Channel(), Array.Empty<SourceConfig>(), new[] { Profile() }).Error);
        Assert.Equal(ProxyPolicyError.SourceAmbiguous, ProxyPolicyResolver.ResolveChannel(Channel(), new[] { Source(), Source("a") }, new[] { Profile() }).Error);
    }

    [Theory]
    [InlineData("https://127.0.0.1:1234", ProxyPolicyError.TransportUnsupported)]
    [InlineData("http://127.0.0.1:1234/path", ProxyPolicyError.EndpointInvalid)]
    [InlineData("http://user:secret@127.0.0.1:1234", ProxyPolicyError.EndpointInvalid)]
    [InlineData("http://127.0.0.1:1234?secret=yes", ProxyPolicyError.EndpointInvalid)]
    [InlineData("socks5://127.0.0.1:1234", ProxyPolicyError.TransportUnsupported)]
    public void RevalidatesWithMutatedEndpoint(string endpoint, ProxyPolicyError error)
    {
        Assert.Equal(error, ProxyPolicyResolver.ResolveSource(Source("a"), new[] { Profile() with { Endpoint = endpoint } }).Error);
    }

    [Fact]
    public void IncompleteAndControlCharacterCredentialsAreInvalid()
    {
        foreach (var profile in new[] { Profile() with { Username = null }, Profile() with { Password = null }, Profile() with { Username = "" }, Profile() with { Password = "bad\r\n" } })
            Assert.Equal(ProxyPolicyError.CredentialsInvalid, ProxyPolicyResolver.ResolveSource(Source("a"), new[] { profile }).Error);
        var longSocks = Profile() with { Kind = ProxyKind.Socks5, Endpoint = "socks5://127.0.0.1:1234", Username = new string('界', 86) };
        Assert.Equal(ProxyPolicyError.CredentialsInvalid, ProxyPolicyResolver.ResolveSource(Source("a"), new[] { longSocks }).Error);
    }

    [Fact]
    public void SnapshotDoesNotPrintOrSerializeCredentialsAndDoesNotFollowProfileMutation()
    {
        var original = Profile();
        var result = ProxyPolicyResolver.ResolveSource(Source("a"), new[] { original });
        var changed = original with { Endpoint = "http://127.0.0.1:19999", Password = "new-secret" };
        Assert.NotEqual(changed.Endpoint, result.Snapshot!.Endpoint.AbsoluteUri.TrimEnd('/'));
        var text = result + " " + result.Snapshot + " " + JsonSerializer.Serialize(result.Snapshot);
        Assert.False(text.Contains(original.Password!, StringComparison.Ordinal));
        Assert.False(text.Contains(original.Username!, StringComparison.Ordinal));
        var failure = ProxyPolicyResolver.ResolveSource(Source("missing"), new[] { original });
        var ex = Assert.Throws<InvalidOperationException>(() => ProxyHttpClientFactory.Create(failure));
        Assert.False(ex.ToString().Contains(original.Password!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OwnedClientIsDisposedWhileBorrowedSystemClientSurvivesLease()
    {
        var policy = ProxyPolicyResolver.ResolveSource(Source(), Array.Empty<UserProxy>());
        var owned = ProxyHttpClientFactory.Create(policy);
        owned.Dispose();
        owned.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owned.Client.GetAsync("http://origin.invalid"));
        using var external = new HttpClient(new StubHandler());
        using (ProxyHttpClientFactory.BorrowSystem(external)) { }
        using var response = await external.GetAsync("http://origin.invalid");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
