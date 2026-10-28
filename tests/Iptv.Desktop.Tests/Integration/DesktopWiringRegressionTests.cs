using System.Net;
using Iptv.Core.Models;
using Iptv.Desktop.Services;
using Iptv.Desktop.ViewModels;
using Xunit;

namespace Iptv.Desktop.Tests.Integration;

public sealed class DesktopWiringRegressionTests
{
    [Theory]
    [InlineData("https://example.invalid/live/user/SECRET/1.ts")]
    [InlineData("https://example.invalid/xtream/movie/user/SECRET/1.mp4")]
    [InlineData("https://example.invalid/series/user/SECRET/1.mkv")]
    [InlineData("https://example.invalid/api?%70assword=SECRET")]
    public void DisplayUrl_DoesNotExposeCredentials(string url)
    {
        Assert.DoesNotContain("SECRET", SourceConfigItemViewModel.SanitizeUrl(url));
    }

    [Fact]
    public async Task SavingFirstSource_LoadsChannelsWithoutAutoplay()
    {
        var (vm, player) = CreateMainWindow();
        await vm.InitializeAsync();
        vm.ConfigsVm.OpenAddM3uDialogCommand.Execute(null);
        vm.ConfigsVm.AddForm.Name = "First";
        vm.ConfigsVm.AddForm.LocationUrl = "https://example.invalid/first.m3u";
        await vm.ConfigsVm.SaveAddFormCommand.ExecuteAsync(null);
        Assert.Single(vm.ConfigsVm.Configs);
        Assert.Equal(2, vm.ChannelsVm.AllChannels.Count);
        Assert.Equal(PlayerState.Idle, player.State);
    }

    [Fact]
    public async Task SelectingAnotherSource_LoadsThatSourcesChannels()
    {
        var (vm, _) = CreateMainWindow();
        foreach (var name in new[] { "first", "second" })
        {
            vm.ConfigsVm.OpenAddM3uDialogCommand.Execute(null);
            vm.ConfigsVm.AddForm.Name = name;
            vm.ConfigsVm.AddForm.LocationUrl = $"https://example.invalid/{name}.m3u";
            await vm.ConfigsVm.SaveAddFormCommand.ExecuteAsync(null);
        }
        vm.ConfigsVm.SelectedConfig = vm.ConfigsVm.Configs.Single(x => x.Name == "first");
        Assert.Equal(2, vm.ChannelsVm.AllChannels.Count);
        vm.ConfigsVm.SelectedConfig = vm.ConfigsVm.Configs.Single(x => x.Name == "second");
        Assert.Single(vm.ChannelsVm.AllChannels);
        Assert.Equal("Second Channel", vm.ChannelsVm.AllChannels[0].Name);
    }

    [Fact]
    public async Task SearchingAfterImport_UsesRealImportedChannels()
    {
        var (vm, _) = CreateMainWindow();
        vm.ConfigsVm.OpenAddM3uDialogCommand.Execute(null);
        vm.ConfigsVm.AddForm.LocationUrl = "https://example.invalid/first.m3u";
        await vm.ConfigsVm.SaveAddFormCommand.ExecuteAsync(null);
        vm.SearchVm.SearchText = "First";
        await vm.SearchVm.PerformSearchAsync();
        Assert.NotEmpty(vm.SearchVm.SearchResults);
    }

    [Fact]
    public async Task SavingSource_PreservesRequestHeaders()
    {
        var library = new MemoryOnlyLibraryService();
        var vm = new ConfigsViewModel(library);
        vm.OpenAddM3uDialogCommand.Execute(null);
        vm.AddForm.LocationUrl = "https://example.invalid/first.m3u";
        vm.AddForm.UserAgent = "synthetic-agent/1";
        vm.AddForm.Referer = "https://example.invalid/";
        await vm.SaveAddFormCommand.ExecuteAsync(null);
        var source = Assert.Single(await library.GetConfigsAsync());
        Assert.NotNull(source.Headers);
        Assert.Equal("synthetic-agent/1", source.Headers["User-Agent"]);
        Assert.Equal("https://example.invalid/", source.Headers["Referer"]);
    }

    private static (MainWindowViewModel Vm, FakePlayer Player) CreateMainWindow()
    {
        MainWindowViewModel? vm = null;
        var library = new MemoryOnlyLibraryService(() =>
            Task.FromResult<IReadOnlyList<Channel>>(vm?.ChannelsVm.AllChannels.ToArray() ?? Array.Empty<Channel>()));
        var player = new FakePlayer();
        var playlist = new CorePlaylistService(new HttpClient(new PlaylistHandler()));
        vm = new MainWindowViewModel(playlist, library, new FakeEpgService(), player);
        return (vm, player);
    }

    private sealed class PlaylistHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.RequestUri!.AbsolutePath.Contains("second")
                ? "#EXTM3U\n#EXTINF:-1,Second Channel\nhttps://example.invalid/second.ts\n"
                : "#EXTM3U\n#EXTINF:-1,First Channel\nhttps://example.invalid/first.ts\n#EXTINF:-1,First Other\nhttps://example.invalid/other.ts\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
        }
    }
}
