using System.Collections.Generic;
using System.Threading.Tasks;
using Iptv.Core.Models;
using Iptv.Desktop.Services;
using Iptv.Desktop.ViewModels;
using Xunit;

namespace Iptv.Desktop.Tests.ViewModels;

public class ConfigsViewModelTests
{
    [Fact]
    public async Task LoadConfigsAsync_LoadsExistingConfigs()
    {
        var existing = new List<SourceConfig>
        {
            new("cfg1", "配置1", SourceKind.M3u, "https://example.com/live.m3u"),
            new("cfg2", "配置2", SourceKind.Xtream, "http://xtream.com:8080", username: "u", password: "p")
        };
        var library = new FakeLibraryService(existing);
        var vm = new ConfigsViewModel(library);

        await vm.InitializeAsync();

        Assert.Equal(2, vm.Configs.Count);
        Assert.Equal("配置1", vm.Configs[0].Name);
    }

    [Fact]
    public void OpenAddM3uDialog_InitializesFormDefaults()
    {
        var library = new FakeLibraryService();
        var vm = new ConfigsViewModel(library);

        vm.OpenAddM3uDialogCommand.Execute(null);

        Assert.True(vm.IsAddDialogOpen);
        Assert.Equal(SourceKind.M3u, vm.AddForm.Kind);
        Assert.Equal("Default", vm.AddForm.Name);
        Assert.Empty(vm.AddForm.LocationUrl);
        // 6 toggles match REA baseline default off
        Assert.False(vm.AddForm.AutoRefresh);
        Assert.False(vm.AddForm.ClosePreview);
        Assert.False(vm.AddForm.AutoRefreshPreview);
        Assert.False(vm.AddForm.CloseDelayDetection);
        Assert.False(vm.AddForm.CloseBuiltinLogo);
        Assert.False(vm.AddForm.HideGroups);
    }

    [Fact]
    public void OpenAddXtreamDialog_InitializesXtreamDefaults()
    {
        var library = new FakeLibraryService();
        var vm = new ConfigsViewModel(library);

        vm.OpenAddXtreamDialogCommand.Execute(null);

        Assert.True(vm.IsAddDialogOpen);
        Assert.Equal(SourceKind.Xtream, vm.AddForm.Kind);
        Assert.Equal("Default", vm.AddForm.Name);
        Assert.Empty(vm.AddForm.ServerUrl);
        Assert.Empty(vm.AddForm.Username);
        Assert.Empty(vm.AddForm.Password);
    }

    [Fact]
    public async Task SaveM3uConfig_EmptyLocation_SetsValidationError()
    {
        var library = new FakeLibraryService();
        var vm = new ConfigsViewModel(library);
        vm.OpenAddM3uDialogCommand.Execute(null);
        vm.AddForm.LocationUrl = "";

        await vm.SaveAddFormCommand.ExecuteAsync(null);

        Assert.True(vm.IsAddDialogOpen);
        Assert.NotNull(vm.AddForm.ValidationErrorMessage);
        Assert.Contains("链接", vm.AddForm.ValidationErrorMessage);
        Assert.Empty(library.Configs);
    }

    [Fact]
    public async Task SaveXtreamConfig_MissingRequired_SetsValidationError()
    {
        var library = new FakeLibraryService();
        var vm = new ConfigsViewModel(library);
        vm.OpenAddXtreamDialogCommand.Execute(null);
        vm.AddForm.ServerUrl = "http://xtream.server:8080";
        vm.AddForm.Username = ""; // missing username
        vm.AddForm.Password = "pass";

        await vm.SaveAddFormCommand.ExecuteAsync(null);

        Assert.True(vm.IsAddDialogOpen);
        Assert.NotNull(vm.AddForm.ValidationErrorMessage);
        Assert.Contains("用户名", vm.AddForm.ValidationErrorMessage);
        Assert.Empty(library.Configs);
    }

    [Fact]
    public async Task SaveValidM3uConfig_SavesToLibraryAndClosesDialog()
    {
        var library = new FakeLibraryService();
        var vm = new ConfigsViewModel(library);
        vm.OpenAddM3uDialogCommand.Execute(null);
        vm.AddForm.Name = "测试源";
        vm.AddForm.LocationUrl = "https://test.com/iptv.m3u";
        vm.AddForm.CustomEpgUrl = "https://test.com/epg.xml";

        await vm.SaveAddFormCommand.ExecuteAsync(null);

        Assert.False(vm.IsAddDialogOpen);
        Assert.Single(library.Configs);
        Assert.Equal("测试源", library.Configs[0].Name);
        Assert.Equal("https://test.com/iptv.m3u", library.Configs[0].Location);
        Assert.Equal("https://test.com/epg.xml", library.Configs[0].CustomEpgUrl);
    }

    [Theory]
    [InlineData("http://user:secret123@server.com:8080/live", "http://server.com:8080")]
    [InlineData("http://server.com/live.m3u8?token=123&password=secret_pass&type=m3u", "http://server.com")]
    [InlineData("https://normal.com/list.m3u", "https://normal.com")]
    [InlineData("http://api.tv/stream?key=secret_key&auth=token_val&secret=my_secret", "http://api.tv")]
    public void SourceConfigItemViewModel_SanitizesSensitiveUrls(string input, string expected)
    {
        var sanitized = SourceConfigItemViewModel.SanitizeUrl(input);
        Assert.Equal(expected, sanitized);
    }
}
