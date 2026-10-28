using System.Text;
using System.Text.Json;
using Iptv.Core.Library;
using Iptv.Core.Models;
using Iptv.Core.Security;

namespace Iptv.Core.Tests.Library;

public sealed class S1CompatibilityTests
{
    [Fact]
    public async Task ProxyAuthentication_RequiresProtection_AndRoundtripsOnlyInsideProtectedPayload()
    {
        using var fixture = new TemporaryLibrary();
        var proxy = new UserProxy("proxy-1", "fixture proxy", ProxyKind.Socks5, "socks5://localhost:1080", "fixture-proxy-user", "fixture-proxy-password");
        var state = new LibraryState { UserProxies = new[] { proxy } };
        var ordinaryJson = JsonSerializer.Serialize(state);
        Assert.DoesNotContain("fixture-proxy-user", ordinaryJson);
        Assert.DoesNotContain("fixture-proxy-password", ordinaryJson);
        Assert.DoesNotContain("fixture-proxy-user", proxy.ToString());
        Assert.DoesNotContain("fixture-proxy-password", proxy.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Plain.SaveAsync(state));
        Assert.False(File.Exists(fixture.Plain.StateFilePath));
        await fixture.Protected.SaveAsync(state);
        var bytes = await File.ReadAllBytesAsync(fixture.Protected.StateFilePath);
        Assert.DoesNotContain("fixture-proxy-password", Encoding.UTF8.GetString(bytes));
        var loaded = await fixture.Protected.LoadAsync();
        Assert.Equal("fixture-proxy-user", Assert.Single(loaded.UserProxies).Username);
        Assert.Equal("fixture-proxy-password", loaded.UserProxies[0].Password);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacySources_MigrateArrayOrderWithoutRewriting_AndStayIdempotent(bool protectedFile)
    {
        using var fixture = new TemporaryLibrary();
        const string legacy = """
            {"Sources":[{"Id":"second","Name":"Second","Kind":0,"Location":"https://example.test/two"},{"Id":"first","Name":"First","Kind":0,"Location":"https://example.test/one"}],"Favorites":[{"Id":"c","SourceId":"second","Name":"Channel","StreamUrl":"https://example.test/c"}],"RecentHistory":[]}
            """;
        var original = protectedFile ? ProtectedBytes(legacy) : Encoding.UTF8.GetBytes(legacy);
        await File.WriteAllBytesAsync(fixture.Plain.StateFilePath, original);
        var store = protectedFile ? fixture.Protected : fixture.Plain;
        var state = await store.LoadAsync();
        Assert.Equal(LibraryState.CurrentSchemaVersion, state.SchemaVersion);
        Assert.Equal(new[] { "second", "first" }, state.Sources.Select(s => s.Id));
        Assert.Equal(new[] { 0, 1 }, state.Sources.Select(s => s.Order));
        Assert.Null(state.Sources[0].ProxyId);
        Assert.Empty(state.UserProxies);
        Assert.Single(state.Favorites);
        Assert.Equal(original, await File.ReadAllBytesAsync(store.StateFilePath));
        await store.SaveAsync(state);
        var migratedBytes = await File.ReadAllBytesAsync(store.StateFilePath);
        var secondLoad = await store.LoadAsync();
        Assert.Equal(new[] { 0, 1 }, secondLoad.Sources.Select(s => s.Order));
        await store.SaveAsync(secondLoad);
        Assert.Equal(migratedBytes, await File.ReadAllBytesAsync(store.StateFilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FutureSchema_IsNotCorruption_AndFreshStoreCannotOverwriteIt(bool protectedFile)
    {
        using var fixture = new TemporaryLibrary();
        const string future = "{\"SchemaVersion\":777,\"FuturePayload\":\"preserve-this\"}";
        var original = protectedFile ? ProtectedBytes(future) : Encoding.UTF8.GetBytes(future);
        await File.WriteAllBytesAsync(fixture.Plain.StateFilePath, original);
        var freshStore = protectedFile ? fixture.Protected : fixture.Plain;
        // Save must inspect the existing file even if this instance has never called Load.
        await Assert.ThrowsAsync<NotSupportedException>(() => freshStore.SaveAsync(new LibraryState()));
        await Assert.ThrowsAsync<NotSupportedException>(() => freshStore.LoadAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(freshStore.StateFilePath));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*corrupt*"));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("favorite")]
    [InlineData("history")]
    public async Task UnknownProxyReferences_FailBeforeWritingRatherThanSilentlyChoosingDirect(string owner)
    {
        using var fixture = new TemporaryLibrary();
        var source = new SourceConfig("s", "source", SourceKind.M3u, "https://example.test/list", proxyId: owner == "source" ? "missing" : null);
        var channel = new Channel("c", "s", "channel", "https://example.test/stream", proxyId: owner == "source" ? null : "missing");
        var state = new LibraryState
        {
            Sources = new[] { source },
            Favorites = owner == "favorite" ? new[] { channel } : Array.Empty<Channel>(),
            RecentHistory = owner == "history" ? new[] { new RecentlyPlayedItem(channel, DateTimeOffset.UnixEpoch) } : Array.Empty<RecentlyPlayedItem>()
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Plain.SaveAsync(state));
        Assert.False(File.Exists(fixture.Plain.StateFilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewFields_RoundtripWithoutResortingOrLosingChannelOverrides(bool protectedFile)
    {
        using var fixture = new TemporaryLibrary();
        var source = new SourceConfig("s", "source", SourceKind.M3u, "https://example.test/list", order: 7, proxyId: "source-proxy");
        var channel = new Channel("c", "s", "channel", "https://example.test/stream", proxyId: "channel-proxy");
        var state = new LibraryState
        {
            Sources = new[] { source }, Favorites = new[] { channel },
            RecentHistory = new[] { new RecentlyPlayedItem(channel, DateTimeOffset.UnixEpoch) },
            UserProxies = new[] { new UserProxy("source-proxy", "HTTP", ProxyKind.Http, "http://localhost:8888"), new UserProxy("channel-proxy", "SOCKS", ProxyKind.Socks5, "socks5://localhost:1080") }
        };
        var store = protectedFile ? fixture.Protected : fixture.Plain;
        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();
        Assert.Equal(7, Assert.Single(loaded.Sources).Order);
        Assert.Equal("source-proxy", loaded.Sources[0].ProxyId);
        Assert.Equal("channel-proxy", Assert.Single(loaded.Favorites).ProxyId);
        Assert.Equal("channel-proxy", Assert.Single(loaded.RecentHistory).Channel.ProxyId);
        Assert.Equal(ProxyKind.Socks5, loaded.UserProxies[1].Kind);
        Assert.Equal("SOCKS", loaded.UserProxies[1].Name);
        Assert.Equal("socks5://localhost:1080", loaded.UserProxies[1].Endpoint);
        Assert.Equal(LibraryState.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Theory]
    [InlineData("http://user:secret@localhost:8888")]
    [InlineData("http://localhost:8888/?token=secret")]
    [InlineData("http://localhost:8888/#secret")]
    public void ProxyEndpoint_RejectsEmbeddedCredentialsOrOpaqueSecrets(string endpoint)
        => Assert.Throws<ArgumentException>(() => new UserProxy("p", "proxy", ProxyKind.Http, endpoint));

    [Fact]
    public async Task RecordWithMutation_CannotBypassProxyEndpointValidationDuringSave()
    {
        using var fixture = new TemporaryLibrary();
        var proxy = new UserProxy("p", "proxy", ProxyKind.Http, "http://localhost:8888") with { Endpoint = "http://localhost:8888/?token=secret" };
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Protected.SaveAsync(new LibraryState { UserProxies = new[] { proxy } }));
        Assert.False(File.Exists(fixture.Plain.StateFilePath));
    }

    [Theory]
    [InlineData("\"SourcePasswords\":null")]
    [InlineData("\"ProxyAuthentication\":null")]
    [InlineData("\"Sources\":null")]
    [InlineData("\"UserProxies\":null")]
    [InlineData("\"ProxyAuthentication\":{\"p\":null}")]
    [InlineData("\"Sources\":[null]")]
    [InlineData("\"UserProxies\":[null]")]
    public async Task MalformedProtectedCollections_FailInControlledPathAndPreserveBackup(string fragment)
    {
        using var fixture = new TemporaryLibrary();
        var original = ProtectedBytes("{\"SchemaVersion\":2," + fragment + "}");
        var store = fixture.Protected;
        await File.WriteAllBytesAsync(store.StateFilePath, original);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(store.StateFilePath));
        var backup = Assert.Single(Directory.GetFiles(fixture.DirectoryPath, "library.corrupt.*.bak"));
        Assert.Equal(original, await File.ReadAllBytesAsync(backup));
    }

    private static byte[] ProtectedBytes(string json) => Encoding.UTF8.GetBytes("IPTV_PROTECTED_V1\n")
        .Concat(new SyntheticProtector().Protect(Encoding.UTF8.GetBytes(json))).ToArray();

    private sealed class TemporaryLibrary : IDisposable
    {
        public string DirectoryPath { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"s1_library_{Guid.NewGuid():N}"));
        public LibraryStateStore Plain => new(DirectoryPath);
        public LibraryStateStore Protected => new(DirectoryPath, new SyntheticProtector());
        public TemporaryLibrary() => Directory.CreateDirectory(DirectoryPath);
        public void Dispose()
        {
            if (Path.GetFullPath(DirectoryPath) == DirectoryPath && Path.GetFileName(DirectoryPath).StartsWith("s1_library_", StringComparison.Ordinal)
                && Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        }
    }

    // Test-only reversible bytes, not a production encryption provider.
    private sealed class SyntheticProtector : ILibraryStateProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] ciphertext) => Protect(ciphertext);
    }
}
