using Iptv.Core.Models;
using Iptv.Core.Security;
using Iptv.Desktop.Services;
using Iptv.Desktop.ViewModels;
using Xunit.Abstractions;

namespace Iptv.Desktop.Tests.Services;

public sealed class ProxyCommitRefreshAcceptanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("save")]
    [InlineData("assign")]
    [InlineData("delete")]
    public async Task PersistedCommitWithFailedRefresh_IsReportedAsCommitted_AndAssignmentDoesNotRevert(string operation)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "proxy_refresh_acceptance_" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var library = new CoreLibraryService(root, new SyntheticProtector());
            var profile = new UserProxy("p", "Before", ProxyKind.Http, "http://proxy.fixture.invalid:8080");
            await library.SaveUserProxyAsync(profile);
            await library.SaveConfigAsync(new SourceConfig("s", "Synthetic source", SourceKind.M3u, "http://origin.invalid/list"));
            var callbackCalls = 0;
            var vm = new ProxyProfilesViewModel(library, () =>
            {
                callbackCalls++;
                return Task.FromException(new IOException("synthetic-refresh-secret"));
            });
            await vm.RefreshAsync();
            SourceProxyAssignment? assignment = null;
            switch (operation)
            {
                case "save":
                    await vm.EditCommand.ExecuteAsync(vm.Profiles.Single());
                    vm.Draft!.Name = "Committed name";
                    await vm.SaveCommand.ExecuteAsync(null);
                    break;
                case "assign":
                    assignment = vm.Sources.Single();
                    assignment.Selected = assignment.Choices.Single(choice => choice.Id == "p");
                    await vm.AssignCommand.ExecuteAsync(assignment);
                    break;
                case "delete":
                    await vm.DeleteCommand.ExecuteAsync(vm.Profiles.Single());
                    break;
            }
            var fresh = new CoreLibraryService(root, new SyntheticProtector());
            var persistedProfiles = await fresh.GetUserProxiesAsync();
            var persistedSource = Assert.Single(await fresh.GetConfigsAsync());
            Assert.Equal(1, callbackCalls);
            if (operation == "save") Assert.Equal("Committed name", Assert.Single(persistedProfiles).Name);
            if (operation == "delete") Assert.Empty(persistedProfiles);
            if (operation == "assign") Assert.Equal("p", persistedSource.ProxyId);
            output.WriteLine($"operation={operation}; freshProfiles={persistedProfiles.Count}; persistedProxy={persistedSource.ProxyId ?? "System"}; selectedProxy={assignment?.Selected?.Id ?? "System"}; message={vm.Message}");
            Assert.False(vm.IsBusy);
            Assert.NotNull(vm.Message);
            Assert.DoesNotContain("synthetic-refresh-secret", vm.Message);
            Assert.Contains(operation == "delete" ? "已删除" : "已保存", vm.Message);
            Assert.Contains("刷新失败", vm.Message);
            if (assignment is not null) Assert.Equal("p", assignment.Selected?.Id);
            if (operation == "save") Assert.Null(vm.Draft);
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (resolved == root && Path.GetDirectoryName(resolved) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                && Path.GetFileName(resolved).StartsWith("proxy_refresh_acceptance_", StringComparison.Ordinal))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class SyntheticProtector : ILibraryStateProtector
    {
        public byte[] Protect(byte[] bytes) => bytes.Select(value => (byte)(value ^ 0x6B)).ToArray();
        public byte[] Unprotect(byte[] bytes) => Protect(bytes);
    }
}
