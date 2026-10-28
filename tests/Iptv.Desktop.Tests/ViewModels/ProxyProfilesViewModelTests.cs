using Iptv.Core.Models;
using Iptv.Desktop.Services;
using Iptv.Desktop.ViewModels;
using Iptv.Desktop.Tests.Integration;

namespace Iptv.Desktop.Tests.ViewModels;

public class ProxyProfilesViewModelTests
{
    [Theory]
    [InlineData("http://proxy.invalid:8080", ProxyKind.Http)]
    [InlineData("http://[::1]:80", ProxyKind.Http)]
    [InlineData("socks5://[::1]:1080", ProxyKind.Socks5)]
    public async Task AddValidProfile_SavesSupportedEndpoint(string endpoint, ProxyKind kind)
    {
        var library = new MemoryOnlyLibraryService();
        var vm = new ProxyProfilesViewModel(library);
        vm.AddCommand.Execute(null);
        vm.Draft!.Name = "Fixture proxy";
        vm.Draft.Kind = kind;
        vm.Draft.Endpoint.Value = endpoint;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.IsEditing);
        Assert.Single(vm.Profiles);
        Assert.Equal(endpoint, Assert.Single(await library.GetUserProxiesAsync()).Endpoint);
        Assert.Contains("已保存", vm.Message);
        Assert.Contains("不表示连接成功", vm.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://proxy.invalid")]
    [InlineData("https://proxy.invalid:8080")]
    [InlineData("http://proxy.invalid:8080/path")]
    [InlineData("http://user:password@proxy.invalid:8080")]
    [InlineData("http://proxy.invalid:8080?fixture=hidden")]
    [InlineData("http://proxy.invalid:8080#fragment")]
    [InlineData("socks5://proxy.invalid:1080")]
    [InlineData("http://proxy.invalid:0")]
    public async Task UnsupportedEndpoint_DoesNotSaveOrPretendSuccess(string endpoint)
    {
        var library = new MemoryOnlyLibraryService();
        var vm = new ProxyProfilesViewModel(library);
        vm.AddCommand.Execute(null);
        vm.Draft!.Name = "Fixture proxy";
        vm.Draft.Endpoint.Value = endpoint;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsEditing);
        Assert.Empty(vm.Profiles);
        Assert.Empty(await library.GetUserProxiesAsync());
        Assert.Contains("仅支持", vm.Message);
        Assert.DoesNotContain("已保存", vm.Message);
    }

    [Fact]
    public async Task EditKeepReplaceClearAndCancel_DoNotExposeOldAuthentication()
    {
        var library = new MemoryOnlyLibraryService();
        var original = Profile();
        await library.SaveUserProxyAsync(original);
        var vm = new ProxyProfilesViewModel(library);
        await vm.RefreshAsync();
        await vm.EditCommand.ExecuteAsync(vm.Profiles.Single());
        Assert.Equal("", vm.Draft!.Username.Value);
        Assert.Equal("", vm.Draft.Password.Value);
        Assert.Equal("", vm.Draft.Endpoint.Value);
        Assert.Equal("http://[::1]:8080", vm.Draft.SafeEndpoint);
        vm.Draft.Name = "Cancelled";
        vm.CancelCommand.Execute(null);
        Assert.Equal(original.Name, Assert.Single(await library.GetUserProxiesAsync()).Name);
        await vm.EditCommand.ExecuteAsync(vm.Profiles.Single());
        vm.Draft!.Name = "Edited";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(original.Password, Assert.Single(await library.GetUserProxiesAsync()).Password);
        await vm.EditCommand.ExecuteAsync(vm.Profiles.Single());
        vm.Draft!.Username.Action = "清除";
        vm.Draft.Password.Action = "替换";
        vm.Draft.Password.Value = " new-fixture-password ";
        await vm.SaveCommand.ExecuteAsync(null);
        var updated = Assert.Single(await library.GetUserProxiesAsync());
        Assert.Null(updated.Username);
        Assert.Equal(" new-fixture-password ", updated.Password);
        await vm.EditCommand.ExecuteAsync(vm.Profiles.Single());
        vm.Draft!.Password.Action = "清除";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(Assert.Single(await library.GetUserProxiesAsync()).Password);
    }

    [Fact]
    public async Task Assignment_CancelAndSaveSystem_AndReferencedDeletion_AreExplicit()
    {
        var source = new SourceConfig("source", "Source name", SourceKind.M3u, "https://example.invalid/list");
        var library = new FakeLibraryService(new[] { source });
        await library.SaveUserProxyAsync(Profile());
        var vm = new ProxyProfilesViewModel(library);
        await vm.RefreshAsync();
        var row = vm.Sources.Single();
        row.Selected = row.Choices.Single(choice => choice.Id == "proxy");
        vm.CancelAssignmentCommand.Execute(row);
        Assert.Null(row.Selected!.Id);
        Assert.Null(Assert.Single(await library.GetConfigsAsync()).ProxyId);
        row.Selected = row.Choices.Single(choice => choice.Id == "proxy");
        await vm.AssignCommand.ExecuteAsync(row);
        Assert.Equal("proxy", Assert.Single(await library.GetConfigsAsync()).ProxyId);
        await vm.DeleteCommand.ExecuteAsync(vm.Profiles.Single());
        Assert.Contains("Source name", vm.Message);
        Assert.Single(vm.Profiles);
        row = vm.Sources.Single();
        row.Selected = row.Choices.Single(choice => choice.Id is null);
        await vm.AssignCommand.ExecuteAsync(row);
        await vm.DeleteCommand.ExecuteAsync(vm.Profiles.Single());
        Assert.Empty(vm.Profiles);
    }

    [Fact]
    public async Task ServiceFailure_LeavesPublishedListsAndDraft_Unchanged()
    {
        var source = new SourceConfig("source", "Source", SourceKind.M3u, "https://example.invalid/list");
        var library = new FailingLibrary(new[] { source });
        await library.SaveUserProxyAsync(Profile());
        var vm = new ProxyProfilesViewModel(library);
        await vm.RefreshAsync();
        await vm.EditCommand.ExecuteAsync(vm.Profiles.Single());
        vm.Draft!.Name = "Unsaved";
        library.Fail = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsEditing);
        Assert.Equal("Fixture proxy", vm.Profiles.Single().Name);
        Assert.DoesNotContain("private-fixture-error", vm.Message);
        await vm.DeleteCommand.ExecuteAsync(vm.Profiles.Single());
        Assert.Single(vm.Profiles);
        var row = vm.Sources.Single();
        row.Selected = row.Choices.Single(choice => choice.Id == "proxy");
        await vm.AssignCommand.ExecuteAsync(row);
        Assert.Null(row.Selected!.Id);
        Assert.Null(Assert.Single(await library.GetConfigsAsync()).ProxyId);
    }

    [Fact]
    public async Task ProfileAndAssignment_FreshProtectedReload_RetainSecretsWithoutPlaintext()
    {
        var root = Path.Combine(Path.GetTempPath(), "iptv-proxy-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var protector = new ConfigEditingAndOrderingTests.ToggleProtector();
        var service = new CoreLibraryService(root, protector);
        try
        {
            await service.SaveConfigAsync(new SourceConfig("source", "Source", SourceKind.M3u, "https://example.invalid/list"));
            var vm = new ProxyProfilesViewModel(service);
            await vm.RefreshAsync();
            vm.AddCommand.Execute(null);
            vm.Draft!.Name = "Saved proxy";
            vm.Draft.Kind = ProxyKind.Socks5;
            vm.Draft.Endpoint.Value = "socks5://[::1]:1080";
            vm.Draft.Username.Action = "替换";
            vm.Draft.Username.Value = "fixture-hidden-user";
            vm.Draft.Password.Action = "替换";
            vm.Draft.Password.Value = "fixture-hidden-password";
            await vm.SaveCommand.ExecuteAsync(null);
            var row = vm.Sources.Single();
            row.Selected = row.Choices.Single(choice => choice.Id is not null);
            await vm.AssignCommand.ExecuteAsync(row);
            var fresh = new CoreLibraryService(root, protector);
            var reloaded = Assert.Single(await fresh.GetUserProxiesAsync());
            Assert.Equal("fixture-hidden-password", reloaded.Password);
            Assert.Equal("fixture-hidden-user", reloaded.Username);
            Assert.Equal(reloaded.Id, Assert.Single(await fresh.GetConfigsAsync()).ProxyId);
            var bytes = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "library.json")));
            Assert.DoesNotContain("fixture-hidden-password", bytes);
            Assert.DoesNotContain("fixture-hidden-user", bytes);
        }
        finally
        {
            var path = Path.GetFullPath(root);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "iptv-proxy-ui-"), path);
            Directory.Delete(path, true);
        }
    }

    [Fact]
    public async Task Editing_UsesCurrentAuthentication_AndUnsupportedExistingEndpointCannotResave()
    {
        var library = new MemoryOnlyLibraryService();
        await library.SaveUserProxyAsync(Profile());
        var vm = new ProxyProfilesViewModel(library);
        await vm.RefreshAsync();
        await vm.EditCommand.ExecuteAsync(vm.Profiles.Single());
        await library.SaveUserProxyAsync(Profile() with { Password = "newer-fixture-auth" });
        vm.Draft!.Name = "Updated name";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("newer-fixture-auth", Assert.Single(await library.GetUserProxiesAsync()).Password);
        var unsupported = Profile() with { Endpoint = "https://[::1]:8080/path" };
        await library.SaveUserProxyAsync(unsupported);
        await vm.RefreshAsync();
        await vm.EditCommand.ExecuteAsync(vm.Profiles.Single());
        vm.Draft!.Name = "Should not save";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsEditing);
        Assert.Contains("仅支持", vm.Message);
        Assert.NotEqual("Should not save", Assert.Single(await library.GetUserProxiesAsync()).Name);
    }

    [Fact]
    public async Task CommittedAssignmentWithRefreshFailure_CancelUsesNewSavedChoice()
    {
        var source = new SourceConfig("source", "Source", SourceKind.M3u, "https://example.invalid/list");
        var library = new FakeLibraryService(new[] { source });
        await library.SaveUserProxyAsync(Profile());
        var vm = new ProxyProfilesViewModel(library, () => Task.FromException(new IOException("private-refresh-error")));
        await vm.RefreshAsync();
        var row = vm.Sources.Single();
        var savedChoice = row.Choices.Single(choice => choice.Id == "proxy");
        row.Selected = savedChoice;
        await vm.AssignCommand.ExecuteAsync(row);
        Assert.Equal(savedChoice.Name, row.CurrentProxyName);
        row.Selected = row.Choices.Single(choice => choice.Id is null);
        vm.CancelAssignmentCommand.Execute(row);
        Assert.Equal("proxy", row.Selected!.Id);
        Assert.Equal("proxy", Assert.Single(await library.GetConfigsAsync()).ProxyId);
        Assert.Contains("已保存", vm.Message);
        Assert.Contains("刷新失败", vm.Message);
        Assert.DoesNotContain("private-refresh-error", vm.Message);
    }

    [Fact]
    public void SafeEndpointSummary_HidesUserInfoPathQuery_AndPreservesIpv6Port()
    {
        Assert.Equal("http://[::1]:8080", ProxyProfilesViewModel.EndpointSummary("http://hidden:secret@[::1]:8080/path?fixture=secret"));
        Assert.Equal("端点已隐藏", ProxyProfilesViewModel.EndpointSummary("invalid-secret"));
    }

    private static UserProxy Profile() => new("proxy", "Fixture proxy", ProxyKind.Http, "http://[::1]:8080", "fixture-user", "fixture-password");
    private sealed class FailingLibrary(IEnumerable<SourceConfig> sources) : FakeLibraryService(sources), ILibraryService
    {
        public bool Fail { get; set; }
        Task ILibraryService.SaveUserProxyAsync(UserProxy profile, CancellationToken ct) => Fail ? Task.FromException(new IOException("private-fixture-error")) : base.SaveUserProxyAsync(profile, ct);
        Task ILibraryService.DeleteUserProxyAsync(string id, CancellationToken ct) => Fail ? Task.FromException(new IOException("private-fixture-error")) : base.DeleteUserProxyAsync(id, ct);
        Task ILibraryService.SetSourceProxyAsync(string sourceId, string? proxyId, CancellationToken ct) => Fail ? Task.FromException(new IOException("private-fixture-error")) : base.SetSourceProxyAsync(sourceId, proxyId, ct);
    }
}
