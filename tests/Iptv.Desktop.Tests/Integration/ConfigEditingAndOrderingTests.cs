using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using System.Security.Cryptography;
using Iptv.Core.Models;
using Iptv.Core.Security;
using Iptv.Desktop.Services;
using Iptv.Desktop.ViewModels;

namespace Iptv.Desktop.Tests.Integration;

public class ConfigEditingAndOrderingTests
{
    [Fact]
    public void EditorAndAtomicOrder_HaveUserActionContracts()
    {
        Assert.NotNull(typeof(ConfigsViewModel).GetProperty("OpenEditCommand"));
        Assert.NotNull(typeof(ILibraryService).GetMethod("SaveSourceOrderAsync"));
    }

    [Fact]
    public async Task AtomicOrder_UpdatesWholeSequence_InMemoryContract()
    {
        ILibraryService service = new MemoryOnlyLibraryService();
        await service.SaveConfigAsync(new SourceConfig("a", "A", SourceKind.M3u, "https://example.invalid/a"));
        await service.SaveConfigAsync(new SourceConfig("b", "B", SourceKind.M3u, "https://example.invalid/b"));
        await service.SaveSourceOrderAsync(new[] { "b", "a" });
        var result = await service.GetConfigsAsync();
        Assert.Equal(new[] { "b", "a" }, result.Select(c => c.Id));
        Assert.Equal(new[] { 0, 1 }, result.Select(c => c.Order));
    }

    [Fact]
    public async Task FailedLibrarySave_RestoresSidecar_AndFreshReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "iptv-task20-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var protector = new ToggleProtector();
        var credentials = new FileCredentials(root, protector);
        var service = new CoreLibraryService(root, protector, credentials);
        var original = new SourceConfig("source", "Before", SourceKind.Xtream, "https://example.invalid", username: "fixture-user", password: "fixture-old");
        try
        {
            await service.SaveConfigAsync(original);
            protector.FailProtect = true;
            await Assert.ThrowsAnyAsync<Exception>(() => service.SaveConfigAsync(original with { Name = "After", Password = "fixture-new" }));
            protector.FailProtect = false;
            Assert.Equal("fixture-old", await credentials.GetPasswordAsync(original.Id));
            var fresh = new CoreLibraryService(root, protector, new FileCredentials(root, protector));
            var reloaded = Assert.Single(await fresh.GetConfigsAsync());
            Assert.Equal("Before", reloaded.Name);
            Assert.Equal("fixture-old", reloaded.Password);
            Assert.Equal("Before", Assert.Single(await service.GetConfigsAsync()).Name);
        }
        finally
        {
            var full = Path.GetFullPath(root);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "iptv-task20-"), full);
            Directory.Delete(full, true);
        }
    }

    [Fact]
    public async Task EditCancel_AndUnchangedSecrets_PreserveMetadataAndCurrentOrder()
    {
        var original = FixtureSource("a") with { Order = 1, ProxyId = "proxy", Headers = new Dictionary<string,string> { ["Referer"] = "fixture-ref", ["X-Keep"] = "fixture-value" } };
        var library = new FakeLibraryService(new[] { original, FixtureSource("b") with { Order = 0 } });
        var vm = new ConfigsViewModel(library);
        await vm.InitializeAsync();
        vm.OpenEditCommand.Execute(vm.Configs.Single(c => c.Id == "a"));
        Assert.All(vm.EditForm!.Fields, f => Assert.Equal("", f.Value));
        Assert.Equal("https://example.invalid", vm.EditForm.SafeLocation);
        vm.EditForm.Name = "Cancelled";
        vm.CancelEditCommand.Execute(null);
        Assert.Equal(original.Name, (await library.GetConfigsAsync()).Single(c => c.Id == "a").Name);
        vm.OpenEditCommand.Execute(vm.Configs.Single(c => c.Id == "a"));
        vm.EditForm!.Name = "Edited";
        await vm.MoveUpCommand.ExecuteAsync(vm.Configs.Single(c => c.Id == "a"));
        await vm.SaveEditCommand.ExecuteAsync(null);
        var saved = (await library.GetConfigsAsync()).Single(c => c.Id == "a");
        Assert.Equal("Edited", saved.Name);
        Assert.Equal(0, saved.Order);
        Assert.Equal("proxy", saved.ProxyId);
        Assert.Equal(original.Password, saved.Password);
        Assert.Equal(original.Location, saved.Location);
        Assert.Equal(original.Username, saved.Username);
        Assert.Equal("fixture-value", saved.Headers!["X-Keep"]);
    }

    [Fact]
    public async Task ExplicitReplaceClear_AndFailure_DoNotPretendSuccess()
    {
        var original = FixtureSource("a");
        var library = new FailureLibrary(new[] { original, FixtureSource("b") });
        var vm = new ConfigsViewModel(library);
        await vm.InitializeAsync();
        vm.OpenEditCommand.Execute(vm.Configs[0]);
        var draft = vm.EditForm!;
        draft.Name = "After";
        draft.Password.Action = "替换";
        draft.Password.Value = "fixture-new";
        draft.Epg.Action = "清除";
        draft.Location.Action = "替换";
        draft.Location.Value = "https://example.invalid/replaced";
        library.Fail = true;
        await vm.SaveEditCommand.ExecuteAsync(null);
        Assert.NotNull(vm.EditError);
        Assert.True(vm.IsEditing);
        Assert.Equal(original.Name, vm.Configs[0].Name);
        Assert.Equal(original.Password, Assert.Single(await library.GetConfigsAsync(), c => c.Id == "a").Password);
        var before = vm.Configs.Select(c => c.Id).ToArray();
        await vm.MoveDownCommand.ExecuteAsync(vm.Configs[0]);
        Assert.Equal(before, vm.Configs.Select(c => c.Id));
        library.Fail = false;
        await vm.SaveEditCommand.ExecuteAsync(null);
        Assert.False(vm.IsEditing);
        var saved = (await library.GetConfigsAsync()).Single(c => c.Id == "a");
        Assert.Equal("fixture-new", saved.Password);
        Assert.Null(saved.CustomEpgUrl);
        Assert.Equal("https://example.invalid/replaced", saved.Location);
    }

    [Fact]
    public async Task Ordering_PersistsAcrossFreshService_RejectsInvalidSets_AndKeepsCacheOnFailure()
    {
        using var fixture = new DiskFixture();
        var service = fixture.Service();
        await service.SaveConfigAsync(FixtureSource("a"));
        await service.SaveConfigAsync(FixtureSource("b"));
        await service.SaveSourceOrderAsync(new[] { "b", "a" });
        Assert.Equal(new[] { "b", "a" }, (await fixture.Service().GetConfigsAsync()).Select(c => c.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => service.SaveSourceOrderAsync(new[] { "a", "a" }));
        await Assert.ThrowsAnyAsync<Exception>(() => service.SaveSourceOrderAsync(new[] { "a" }));
        await Assert.ThrowsAnyAsync<Exception>(() => service.SaveSourceOrderAsync(new[] { "a", "unknown" }));
        fixture.Protector.FailProtect = true;
        await Assert.ThrowsAnyAsync<Exception>(() => service.SaveSourceOrderAsync(new[] { "a", "b" }));
        fixture.Protector.FailProtect = false;
        Assert.Equal(new[] { "b", "a" }, (await service.GetConfigsAsync()).Select(c => c.Id));
        Assert.Equal(new[] { "b", "a" }, (await fixture.Service().GetConfigsAsync()).Select(c => c.Id));
        var existing = (await service.GetConfigsAsync()).Single(c => c.Id == "b");
        await service.SaveConfigAsync(existing with { Name = "Renamed", Order = 99, ProxyId = "stale" });
        Assert.Equal(0, (await service.GetConfigsAsync())[0].Order);
        Assert.Null((await service.GetConfigsAsync())[0].ProxyId);
    }

    [Fact]
    public async Task ExplicitPasswordClear_DeletesSidecar_AndCancelFailureRestoresIt()
    {
        using var fixture = new DiskFixture();
        var service = fixture.Service();
        var source = FixtureSource("a");
        await service.SaveConfigAsync(source);
        fixture.Protector.FailProtect = true;
        await Assert.ThrowsAnyAsync<Exception>(() => service.SaveConfigAsync(source with { Password = null }));
        fixture.Protector.FailProtect = false;
        Assert.Equal(source.Password, await fixture.Credentials.GetPasswordAsync(source.Id));
        await service.SaveConfigAsync(source with { Password = null });
        Assert.Null(await fixture.Credentials.GetPasswordAsync(source.Id));
        Assert.Null(Assert.Single(await fixture.Service().GetConfigsAsync()).Password);
    }

    [Fact]
    public async Task SavedDraft_ReopensFromProtectedDisk_WithPreservedAuthentication()
    {
        using var fixture = new DiskFixture();
        var source = FixtureSource("a") with { Headers = new Dictionary<string, string> { ["X-Preserved"] = "fixture-header", ["Referer"] = "fixture-original" } };
        var service = fixture.Service();
        await service.SaveConfigAsync(source);
        var vm = new ConfigsViewModel(service);
        await vm.InitializeAsync();
        vm.OpenEditCommand.Execute(vm.Configs[0]);
        vm.EditForm!.Name = "Persisted edit";
        vm.EditForm.UserAgent.Action = "替换";
        vm.EditForm.UserAgent.Value = "FixtureAgent/1";
        vm.EditForm.Referer.Action = "清除";
        await vm.SaveEditCommand.ExecuteAsync(null);
        Assert.Null(vm.EditError);
        var fresh = Assert.Single(await fixture.Service().GetConfigsAsync());
        Assert.Equal("Persisted edit", fresh.Name);
        Assert.Equal(source.Id, fresh.Id);
        Assert.Equal(source.Password, fresh.Password);
        Assert.Equal(source.Username, fresh.Username);
        Assert.Equal(source.Location, fresh.Location);
        Assert.Equal("fixture-header", fresh.Headers!["X-Preserved"]);
        Assert.Equal("FixtureAgent/1", fresh.Headers["User-Agent"]);
        Assert.False(fresh.Headers.ContainsKey("Referer"));
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Root, "library.json"));
        Assert.DoesNotContain("fixture-old", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("fixture-token", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task CancelAfterCredentialWrite_RestoresOldSidecar_WithoutCancelledRollback()
    {
        using var fixture = new DiskFixture();
        var source = FixtureSource("a");
        var service = fixture.Service();
        await service.SaveConfigAsync(source);
        using var cancellation = new CancellationTokenSource();
        fixture.Credentials.AfterSave = () => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveConfigAsync(source with { Password = "fixture-new" }, cancellation.Token));
        fixture.Credentials.AfterSave = null;
        Assert.Equal(source.Password, await fixture.Credentials.GetPasswordAsync(source.Id));
        Assert.Equal(source.Password, Assert.Single(await fixture.Service().GetConfigsAsync()).Password);
    }

    [Fact]
    public async Task FailedNewSourceSave_RemovesNewSidecar_AndDoesNotPublishSource()
    {
        using var fixture = new DiskFixture();
        var service = fixture.Service();
        fixture.Protector.FailProtect = true;
        await Assert.ThrowsAnyAsync<Exception>(() => service.SaveConfigAsync(FixtureSource("a")));
        fixture.Protector.FailProtect = false;
        Assert.Null(await fixture.Credentials.GetPasswordAsync("a"));
        Assert.Empty(await service.GetConfigsAsync());
        Assert.Empty(await fixture.Service().GetConfigsAsync());
    }

    [Fact]
    public async Task AtomicOrder_PerformsOneLibraryProtection_AndNoCredentialWrites()
    {
        using var fixture = new DiskFixture();
        var service = fixture.Service();
        await service.SaveConfigAsync(FixtureSource("a"));
        await service.SaveConfigAsync(FixtureSource("b"));
        var before = fixture.Protector.ProtectCalls;
        var sidecars = Directory.GetFiles(fixture.Root, "*.cred").ToDictionary(path => path, File.ReadAllBytes);
        await service.SaveSourceOrderAsync(new[] { "b", "a" });
        Assert.Equal(before + 1, fixture.Protector.ProtectCalls);
        Assert.All(sidecars, sidecar => Assert.Equal(sidecar.Value, File.ReadAllBytes(sidecar.Key)));
    }

    [Fact]
    public async Task DraftSave_UsesLatestMetadata_AndRejectsDeletedSource()
    {
        var library = new FakeLibraryService(new[] { FixtureSource("a") });
        var vm = new ConfigsViewModel(library);
        await vm.InitializeAsync();
        vm.OpenEditCommand.Execute(vm.Configs[0]);
        var current = Assert.Single(await library.GetConfigsAsync());
        current = current with { Order = 5, ProxyId = "latest-proxy" };
        // Simulates another editor changing metadata after this draft opened.
        var live = new FakeLibraryService(new[] { current });
        var draft = vm.EditForm!;
        draft.Name = "Edited";
        var applied = draft.Apply(Assert.Single(await live.GetConfigsAsync()));
        Assert.Equal(5, applied.Order);
        Assert.Equal("latest-proxy", applied.ProxyId);
        await library.DeleteConfigAsync("a");
        await vm.SaveEditCommand.ExecuteAsync(null);
        Assert.True(vm.IsEditing);
        Assert.NotNull(vm.EditError);
        Assert.Empty(await library.GetConfigsAsync());
    }

    [AvaloniaFact]
    public async Task ActualConfigControls_EditSaveCancelAndMove_AreConnectedAndDoNotRevealOldSecrets()
    {
        var library = new FakeLibraryService(new[] { FixtureSource("a"), FixtureSource("b") with { Order = 1 } });
        var vm = new MainWindowViewModel(new FakePlaylistService(), library, new FakeEpgService(), new FakePlayer());
        var window = new Iptv.Desktop.MainWindow { DataContext = vm };
        try
        {
            window.Show();
            await window.InitializationTask;
            vm.NavigateToConfigsCommand.Execute(null);
            window.UpdateLayout();
            var item = vm.ConfigsVm.Configs.Single(c => c.Id == "a");
            var edit = window.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext == item && button.Command == vm.ConfigsVm.OpenEditCommand);
            edit.Command!.Execute(edit.CommandParameter);
            window.UpdateLayout();
            Assert.True(window.FindControl<Border>("EditConfigOverlay")!.IsEffectivelyVisible);
            Assert.All(window.GetVisualDescendants().OfType<TextBlock>(), text => Assert.DoesNotContain("fixture-old", text.Text ?? ""));
            Assert.All(window.GetVisualDescendants().OfType<TextBox>(), text => Assert.DoesNotContain("fixture-token", text.Text ?? ""));
            window.FindControl<TextBox>("EditConfigName")!.Text = "UI edited";
            await vm.ConfigsVm.SaveEditCommand.ExecuteAsync(null);
            Assert.Equal("UI edited", (await library.GetConfigsAsync()).Single(c => c.Id == "a").Name);
            Assert.False(vm.ConfigsVm.IsEditing);
            await vm.ConfigsVm.MoveDownCommand.ExecuteAsync(vm.ConfigsVm.Configs.Single(c => c.Id == "a"));
            Assert.Equal(new[] { "b", "a" }, vm.ConfigsVm.Configs.Select(c => c.Id));
            Assert.Equal("a", vm.ConfigsVm.SelectedConfig!.Id);
            vm.ConfigsVm.OpenEditCommand.Execute(vm.ConfigsVm.Configs[0]);
            window.FindControl<Button>("CancelEditConfigButton")!.Command!.Execute(null);
            Assert.False(vm.ConfigsVm.IsEditing);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData("不修改", "fixture-old")]
    [InlineData("清除", null)]
    [InlineData("替换", " fixture-new ")]
    public void PasswordActions_AreExplicit_AndPreserveReplacementWhitespace(string action, string? expected)
    {
        var draft = new ConfigEditForm(FixtureSource("a"));
        draft.Password.Action = action;
        draft.Password.Value = " fixture-new ";
        Assert.Equal(expected, draft.Apply(FixtureSource("a")).Password);
        draft.Location.Action = "清除";
        Assert.Throws<ArgumentException>(() => draft.Apply(FixtureSource("a")));
    }

    [Fact]
    public async Task MoveBoundaries_AreNoOps_AndCancelledOrderDoesNotMutate()
    {
        ILibraryService library = new MemoryOnlyLibraryService();
        await library.SaveConfigAsync(FixtureSource("a"));
        var vm = new ConfigsViewModel(library);
        await vm.InitializeAsync();
        await vm.MoveUpCommand.ExecuteAsync(vm.Configs[0]);
        await vm.MoveDownCommand.ExecuteAsync(vm.Configs[0]);
        Assert.Null(vm.EditError);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.SaveSourceOrderAsync(new[] { "a" }, cancellation.Token));
        Assert.Equal("a", Assert.Single(await library.GetConfigsAsync()).Id);
    }

    private static SourceConfig FixtureSource(string id) => new(id, "Source " + id, SourceKind.Xtream,
        "https://example.invalid/list?fixture=fixture-token", customEpgUrl: "https://example.invalid/epg", username: "fixture-user", password: "fixture-old");

    private sealed class FailureLibrary(IEnumerable<SourceConfig> sources) : FakeLibraryService(sources), ILibraryService
    {
        public bool Fail { get; set; }
        Task ILibraryService.SaveConfigAsync(SourceConfig config, CancellationToken ct)
            => Fail ? Task.FromException(new IOException("Synthetic failure")) : base.SaveConfigAsync(config, ct);
        Task ILibraryService.SaveSourceOrderAsync(IReadOnlyList<string> ids, CancellationToken ct)
            => Fail ? Task.FromException(new IOException("Synthetic failure")) : base.SaveSourceOrderAsync(ids, ct);
    }

    private sealed class DiskFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "iptv-task20-" + Guid.NewGuid().ToString("N"));
        public ToggleProtector Protector { get; } = new();
        public FileCredentials Credentials { get; }
        public DiskFixture() { Directory.CreateDirectory(Root); Credentials = new FileCredentials(Root, Protector); }
        public CoreLibraryService Service() => new(Root, Protector, Credentials);
        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "iptv-task20-"), path);
            Directory.Delete(path, true);
        }
    }

    internal sealed class ToggleProtector : ILibraryStateProtector
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public bool FailProtect { get; set; }
        public int ProtectCalls { get; private set; }
        public byte[] Protect(byte[] plaintext)
        {
            ProtectCalls++;
            if (FailProtect) throw new IOException("Synthetic persistence failure");
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(nonce, plaintext, cipher, tag);
            return nonce.Concat(tag).Concat(cipher).ToArray();
        }
        public byte[] Unprotect(byte[] protectedData)
        {
            using var aes = new AesGcm(_key, 16);
            var plain = new byte[protectedData.Length - 28];
            aes.Decrypt(protectedData.AsSpan(0, 12), protectedData.AsSpan(28), protectedData.AsSpan(12, 16), plain);
            return plain;
        }
    }

    internal sealed class FileCredentials(string root, ToggleProtector protector) : ICredentialStorage
    {
        public Action? AfterSave { get; set; }
        private string PathFor(string id) => Path.Combine(root, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id))) + ".cred");
        public Task SavePasswordAsync(string id, string password, CancellationToken cancellationToken = default)
        {
            // Independent sidecar protection remains operational during library-store injected failure.
            var failed = protector.FailProtect;
            protector.FailProtect = false;
            try { File.WriteAllBytes(PathFor(id), protector.Protect(System.Text.Encoding.UTF8.GetBytes(password))); }
            finally { protector.FailProtect = failed; }
            AfterSave?.Invoke();
            return Task.CompletedTask;
        }
        public Task<string?> GetPasswordAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(File.Exists(PathFor(id)) ? System.Text.Encoding.UTF8.GetString(protector.Unprotect(File.ReadAllBytes(PathFor(id)))) : null);
        public Task DeletePasswordAsync(string id, CancellationToken cancellationToken = default)
        {
            var path = Path.GetFullPath(PathFor(id));
            if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException();
            if (File.Exists(path)) File.Delete(path);
            return Task.CompletedTask;
        }
    }
}
