using Iptv.Core.Models;
using Iptv.Desktop.Services;

namespace Iptv.Desktop.Tests.Services;

public class LocalMediaTests
{
    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mkv")]
    [InlineData(".ts")]
    [InlineData(".mov")]
    [InlineData(".mp3")]
    public async Task LocalFactory_UsesBasenameFileUriAndNoProxy(string extension)
    {
        using var fixture = new LocalFixture(extension);
        var channel = LocalMediaService.CreateChannel(fixture.FilePath);
        Assert.Equal(Path.GetFileName(fixture.FilePath), channel.Name);
        Assert.Equal(LocalMediaService.SourceMarker, channel.SourceId);
        Assert.Null(channel.ProxyId);
        Assert.Null(channel.Headers);
        Assert.Equal(fixture.FilePath, new Uri(channel.StreamUrl).LocalPath);
        await new CoreProxyPolicyService().CheckNativePlaybackAsync(channel);
    }

    [Theory]
    [InlineData("file://remote.invalid/share/movie.mp4")]
    [InlineData("file://localhost/share/movie.mp4")]
    [InlineData("file:////remote.invalid/share/movie.mp4")]
    [InlineData("\\\\remote.invalid\\share\\movie.mp4")]
    [InlineData("//remote.invalid/share/movie.mp4")]
    [InlineData("https://remote.invalid/movie.mp4")]
    [InlineData("smb://remote.invalid/share/movie.mp4")]
    [InlineData("relative/movie.mp4")]
    public async Task RemoteOrRelativeCandidates_NeverStartNative(string input)
    {
        Assert.Throws<InvalidOperationException>(() => LocalMediaService.CreateChannel(input));
        var starts = 0;
        var policy = new CoreProxyPolicyService(new FakeLibraryService());
        using var session = new LibVlcPlayerSession(_ => { starts++; return true; }, policy.CheckNativePlaybackAsync);
        await session.PlayAsync(new Channel("rejected", LocalMediaService.SourceMarker, "Rejected", input));
        Assert.Equal(0, starts);
        Assert.Equal(PlayerState.Error, session.State);
        Assert.DoesNotContain("remote.invalid", session.ErrorMessage ?? "");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("deleted")]
    [InlineData("empty")]
    [InlineData("unsupported")]
    public async Task InvalidFiles_NeverStartNative(string scenario)
    {
        using var fixture = new LocalFixture(scenario == "unsupported" ? ".m3u" : ".mp4");
        var input = fixture.FilePath;
        if (scenario is "missing" or "deleted") File.Delete(input);
        if (scenario == "empty") File.WriteAllBytes(input, Array.Empty<byte>());
        if (scenario == "directory") input = fixture.Root;
        Assert.Throws<InvalidOperationException>(() => LocalMediaService.CreateChannel(input));
        var starts = 0;
        using var session = new LibVlcPlayerSession(_ => { starts++; return true; }, new CoreProxyPolicyService(new FakeLibraryService()).CheckNativePlaybackAsync);
        await session.PlayAsync(new Channel("rejected", LocalMediaService.SourceMarker, "Rejected", new Uri(input).AbsoluteUri));
        Assert.Equal(0, starts);
        Assert.Equal(PlayerState.Error, session.State);
    }

    [Theory]
    [InlineData("proxy")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ExplicitProxy_EvenBlank_NeverGetsLocalBypass(string proxy)
    {
        using var fixture = new LocalFixture();
        var channel = LocalMediaService.CreateChannel(fixture.FilePath) with { ProxyId = proxy };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CoreProxyPolicyService(new FakeLibraryService()).CheckNativePlaybackAsync(channel));
    }

    [Fact]
    public async Task SourceCollisionAndInheritedProxy_CannotGetLocalBypass()
    {
        using var fixture = new LocalFixture();
        var channel = LocalMediaService.CreateChannel(fixture.FilePath);
        var source = new SourceConfig(LocalMediaService.SourceMarker, "Collision", SourceKind.M3u, "https://example.invalid/list", proxyId: "proxy");
        var library = new FakeLibraryService(new[] { source });
        await library.SaveUserProxyAsync(new UserProxy("proxy", "Proxy", ProxyKind.Http, "http://proxy.invalid:8080"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CoreProxyPolicyService(library).CheckNativePlaybackAsync(channel));
        var otherSource = source with { Id = "existing-source" };
        library = new FakeLibraryService(new[] { otherSource });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CoreProxyPolicyService(library).CheckNativePlaybackAsync(channel with { SourceId = otherSource.Id }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CoreProxyPolicyService().CheckNativePlaybackAsync(channel with { Headers = new Dictionary<string,string> { ["Proxy-Authorization"] = "fixture-only" } }));
    }

    [Fact]
    public async Task EscapedLocalName_RoundTripsWithoutExposingPath_AndFileLinkIsRejected()
    {
        using var fixture = new LocalFixture();
        var special = Path.Combine(fixture.Root, "合成 # %20.mov");
        await File.WriteAllBytesAsync(special, new byte[] { 1, 2, 3 });
        var channel = LocalMediaService.CreateChannel(special);
        Assert.Equal(special, new Uri(channel.StreamUrl).LocalPath);
        Assert.Equal("合成 # %20.mov", channel.Name);
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(fixture.Root, "linked.mp4");
            File.CreateSymbolicLink(link, fixture.FilePath);
            Assert.Throws<InvalidOperationException>(() => LocalMediaService.CreateChannel(link));
        }
    }

    internal sealed class LocalFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "local-media-" + Guid.NewGuid().ToString("N"));
        public string FilePath { get; }
        public LocalFixture(string extension = ".mp4")
        {
            Directory.CreateDirectory(Root);
            FilePath = Path.Combine(Root, "Synthetic media" + extension);
            File.WriteAllBytes(FilePath, new byte[] { 0, 1, 2 });
        }
        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "local-media-"), path);
            Directory.Delete(path, true);
        }
    }

    [Fact]
    public async Task IndependentLocalFile_PassesPreflightBeforeNativeStart()
    {
        var file = Path.Combine(Path.GetTempPath(), "local-media-" + Guid.NewGuid().ToString("N") + ".mp4");
        await File.WriteAllBytesAsync(file, new byte[] { 0, 1, 2 });
        try
        {
            var policy = new CoreProxyPolicyService(new FakeLibraryService());
            var channel = new Channel("local-test", "local-media", Path.GetFileName(file), new Uri(file).AbsoluteUri);
            var starts = 0;
            var session = new LibVlcPlayerSession(_ => { starts++; return true; }, policy.CheckNativePlaybackAsync);
            using (session)
            {
                await session.PlayAsync(channel);
                Assert.Equal(1, starts);
                Assert.Equal(channel.Name, session.CurrentChannel?.Name);
                Assert.Equal(PlayerState.Loading, session.State); // Native Playing event is intentionally not simulated.
            }
        }
        finally
        {
            var path = Path.GetFullPath(file);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "local-media-"), path);
            File.Delete(path);
        }
    }
}
