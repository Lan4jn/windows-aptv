using System.Text;
using Iptv.Core.Library;
using Iptv.Core.Models;
using Iptv.Core.Security;
using Iptv.Desktop.Services;

namespace Iptv.Desktop.Tests.Services;

public sealed class ProxyLibraryServiceTests
{
    [Fact]
    public async Task ClearHistory_PreservesProxyProfilesSourceOrderAndCredentialsAfterFreshReload()
    {
        using var fixture = new TemporaryLibrary();
        var source = new SourceConfig("s", "fixture", SourceKind.M3u, "http://example.test/list", order: 7, proxyId: "p");
        var proxy = new UserProxy("p", "fixture proxy", ProxyKind.Http, "http://localhost:8888", "fixture-user", "fixture-pass");
        var channel = new Channel("c", "s", "fixture channel", "http://example.test/stream");
        await fixture.Store.SaveAsync(new LibraryState
        {
            Sources = new[] { source }, UserProxies = new[] { proxy },
            RecentHistory = new[] { new RecentlyPlayedItem(channel, DateTimeOffset.UnixEpoch) }
        });
        var service = fixture.Service;
        await service.ClearHistoryAsync();
        var reloaded = await fixture.Store.LoadAsync();
        Assert.Empty(reloaded.RecentHistory);
        Assert.Equal(7, Assert.Single(reloaded.Sources).Order);
        Assert.Equal("p", reloaded.Sources[0].ProxyId);
        Assert.Equal("fixture-pass", Assert.Single(reloaded.UserProxies).Password);
    }

    [Theory]
    [InlineData("core")]
    [InlineData("memory")]
    [InlineData("fake")]
    public async Task ProxyCrud_AssignmentAndReferencedDeletionHaveSameContract(string adapter)
    {
        using var fixture = new TemporaryLibrary();
        ILibraryService service = adapter switch
        {
            "core" => fixture.Service,
            "memory" => new MemoryOnlyLibraryService(),
            _ => new FakeLibraryService()
        };
        await service.SaveConfigAsync(new SourceConfig("s", "source", SourceKind.M3u, "http://example.test/list"));
        var proxy = new UserProxy("p", "named proxy", ProxyKind.Http, "http://localhost:8888", "fixture-user", "fixture-pass");
        await service.SaveUserProxyAsync(proxy);
        await service.SetSourceProxyAsync("s", "p");
        var snapshot = await service.GetProxyPolicyContextAsync();
        Assert.Equal("p", Assert.Single(snapshot.Sources).ProxyId);
        Assert.Equal("fixture-pass", Assert.Single(snapshot.Proxies).Password);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteUserProxyAsync("p"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetSourceProxyAsync("s", "unknown"));
        Assert.Equal("p", Assert.Single(await service.GetConfigsAsync()).ProxyId);
        await service.SetSourceProxyAsync("s", null);
        await service.DeleteUserProxyAsync("p");
        Assert.Empty(await service.GetUserProxiesAsync());
        if (adapter == "core")
        {
            Assert.Empty(await fixture.Service.GetUserProxiesAsync());
            Assert.Null(Assert.Single(await fixture.Service.GetConfigsAsync()).ProxyId);
        }
    }

    [Fact]
    public async Task ProxySecretSaveWithoutProtector_FailsWithoutChangingFileOrCachedProfiles()
    {
        using var fixture = new TemporaryLibrary();
        var service = new CoreLibraryService(fixture.DirectoryPath);
        await service.SaveConfigAsync(new SourceConfig("s", "source", SourceKind.M3u, "http://example.test/list"));
        var original = await File.ReadAllBytesAsync(fixture.Store.StateFilePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveUserProxyAsync(new UserProxy("p", "proxy", ProxyKind.Http, "http://localhost:8888", "fixture-user", "fixture-pass")));
        Assert.Empty(await service.GetUserProxiesAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Store.StateFilePath));
    }

    [Fact]
    public async Task ProtectedProxyCrud_ReloadsAuthenticationAndRejectsCancelledMutation()
    {
        using var fixture = new TemporaryLibrary();
        var service = fixture.Service;
        await service.SaveUserProxyAsync(new UserProxy("p", "proxy", ProxyKind.Socks5, "socks5://localhost:1080", "fixture-user", "fixture-pass"));
        var reloaded = await fixture.Service.GetUserProxiesAsync();
        Assert.Equal("fixture-user", Assert.Single(reloaded).Username);
        Assert.Equal("fixture-pass", reloaded[0].Password);
        Assert.DoesNotContain("fixture-pass", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fixture.Store.StateFilePath)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DeleteUserProxyAsync("p", cancellation.Token));
        Assert.Single(await service.GetUserProxiesAsync());
    }

    [Fact]
    public async Task NativePolicy_AllowsOnlySystemAndRejectsResolvedOrMissingReference()
    {
        var source = new SourceConfig("s", "source", SourceKind.M3u, "http://fixture.invalid/list");
        var channel = new Channel("c", "s", "channel", "http://fixture.invalid/stream");
        var library = new MemoryOnlyLibraryService();
        await library.SaveConfigAsync(source);
        var policy = new CoreProxyPolicyService(library);
        await policy.CheckNativePlaybackAsync(channel);
        await library.SaveUserProxyAsync(new UserProxy("p", "proxy", ProxyKind.Http, "http://localhost:8888"));
        await library.SetSourceProxyAsync("s", "p");
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.CheckNativePlaybackAsync(channel));
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.CheckNativePlaybackAsync(channel with { ProxyId = "missing" }));
        await library.SetSourceProxyAsync("s", null);
        await policy.CheckNativePlaybackAsync(channel);
        await library.DeleteConfigAsync("s");
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.CheckNativePlaybackAsync(channel));
    }

    [Theory]
    [InlineData("core")]
    [InlineData("memory")]
    public async Task DeleteProxy_ReentrantProviderReadsSameLibraryAndCompletes(string adapter)
    {
        using var fixture = new TemporaryLibrary();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        ILibraryService service = null!;
        var providerCompleted = false;
        async Task<IReadOnlyList<Channel>> Provider()
        {
            await service.GetConfigsAsync(cancellation.Token);
            providerCompleted = true;
            return Array.Empty<Channel>();
        }
        service = adapter == "core" ? fixture.CreateService(Provider) : new MemoryOnlyLibraryService(Provider);
        await service.SaveConfigAsync(new SourceConfig("s", "source", SourceKind.M3u, "http://fixture.invalid/list"));
        await service.SaveUserProxyAsync(new UserProxy("p", "proxy", ProxyKind.Http, "http://localhost:8888"));
        await service.DeleteUserProxyAsync("p", cancellation.Token);
        Assert.True(providerCompleted);
        Assert.Empty(await service.GetUserProxiesAsync());
        if (adapter == "core") Assert.Empty(await fixture.Service.GetUserProxiesAsync());
    }

    [Theory]
    [InlineData("core")]
    [InlineData("memory")]
    public async Task DeleteProxy_CancelledDuringProviderDoesNotCommitAndDoesNotHoldLibraryLock(string adapter)
    {
        using var fixture = new TemporaryLibrary();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TaskCompletionSource<IReadOnlyList<Channel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<Channel>> Provider() { entered.TrySetResult(); return provider.Task; }
        ILibraryService service = adapter == "core" ? fixture.CreateService(Provider) : new MemoryOnlyLibraryService(Provider);
        await service.SaveUserProxyAsync(new UserProxy("p", "proxy", ProxyKind.Http, "http://localhost:8888"));
        var original = adapter == "core" ? await File.ReadAllBytesAsync(fixture.Store.StateFilePath) : null;
        using var caller = new CancellationTokenSource();
        var deletion = service.DeleteUserProxyAsync("p", caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deletion.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Single(await service.GetUserProxiesAsync().WaitAsync(TimeSpan.FromSeconds(2)));
            if (original != null) Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Store.StateFilePath));
        }
        finally
        {
            provider.TrySetResult(Array.Empty<Channel>());
            try { await deletion; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("core")]
    [InlineData("memory")]
    public async Task DeleteProxy_RechecksLatestReferencesAfterProviderAndPreservesStateOnFailure(string adapter)
    {
        using var fixture = new TemporaryLibrary();
        ILibraryService service = null!;
        async Task<IReadOnlyList<Channel>> Provider()
        {
            await service.SetSourceProxyAsync("s", "p");
            return Array.Empty<Channel>();
        }
        service = adapter == "core" ? fixture.CreateService(Provider) : new MemoryOnlyLibraryService(Provider);
        await service.SaveConfigAsync(new SourceConfig("s", "source", SourceKind.M3u, "http://fixture.invalid/list"));
        await service.SaveUserProxyAsync(new UserProxy("p", "proxy", ProxyKind.Http, "http://localhost:8888"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteUserProxyAsync("p").WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Single(await service.GetUserProxiesAsync());
        Assert.Equal("p", Assert.Single(await service.GetConfigsAsync()).ProxyId);
        if (adapter == "core")
        {
            Assert.Single(await fixture.Service.GetUserProxiesAsync());
            Assert.Equal("p", Assert.Single(await fixture.Service.GetConfigsAsync()).ProxyId);
        }
    }

    [Theory]
    [InlineData("core")]
    [InlineData("memory")]
    public async Task DeleteProxy_ProviderFailureDoesNotCommit(string adapter)
    {
        using var fixture = new TemporaryLibrary();
        Task<IReadOnlyList<Channel>> Provider() => Task.FromException<IReadOnlyList<Channel>>(new IOException("Synthetic provider failure."));
        ILibraryService service = adapter == "core" ? fixture.CreateService(Provider) : new MemoryOnlyLibraryService(Provider);
        await service.SaveUserProxyAsync(new UserProxy("p", "proxy", ProxyKind.Http, "http://localhost:8888"));
        var original = adapter == "core" ? await File.ReadAllBytesAsync(fixture.Store.StateFilePath) : null;
        await Assert.ThrowsAsync<IOException>(() => service.DeleteUserProxyAsync("p"));
        Assert.Single(await service.GetUserProxiesAsync());
        if (original != null) Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Store.StateFilePath));
    }

    private sealed class TemporaryLibrary : IDisposable
    {
        private readonly ILibraryStateProtector _protector = new SyntheticProtector();
        public string DirectoryPath { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"proxy_service_{Guid.NewGuid():N}"));
        public LibraryStateStore Store => new(DirectoryPath, _protector);
        public CoreLibraryService Service => new(DirectoryPath, _protector);
        public CoreLibraryService CreateService(Func<Task<IReadOnlyList<Channel>>> provider)
            => new(DirectoryPath, _protector, channelsProvider: provider);
        public TemporaryLibrary() => Directory.CreateDirectory(DirectoryPath);
        public void Dispose()
        {
            if (Path.GetFullPath(DirectoryPath) == DirectoryPath && Path.GetFileName(DirectoryPath).StartsWith("proxy_service_", StringComparison.Ordinal)
                && Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        }
    }

    private sealed class SyntheticProtector : ILibraryStateProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] ciphertext) => Protect(ciphertext);
    }
}
