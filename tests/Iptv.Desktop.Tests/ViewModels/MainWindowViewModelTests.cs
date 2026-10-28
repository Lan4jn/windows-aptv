using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Iptv.Core.Models;
using Iptv.Desktop.Services;
using Iptv.Desktop.ViewModels;
using Xunit;

namespace Iptv.Desktop.Tests.ViewModels;

public class MainWindowViewModelTests
{
    [Fact]
    public void InitialState_DefaultsToChannelsSection()
    {
        var vm = new MainWindowViewModel();

        Assert.Equal(NavigationSection.Channels, vm.CurrentSection);
        Assert.Equal("IPTV 桌面播放器 - 频道", vm.Title);
        Assert.NotNull(vm.ChannelsVm);
        Assert.NotNull(vm.FavoritesVm);
        Assert.NotNull(vm.ConfigsVm);
        Assert.NotNull(vm.SettingsVm);
        Assert.NotNull(vm.SearchVm);
        Assert.NotNull(vm.PlayerVm);
    }

    [Fact]
    public async Task InitializeAsync_RestoresConfigsAndLoadsChannels_WithoutAutoplay()
    {
        var config = new SourceConfig("src1", "默认测试源", SourceKind.M3u, "http://example.com/live.m3u");
        var channels = new List<Channel>
        {
            new("ch1", "src1", "CCTV-1", "http://live.com/1.m3u8"),
            new("ch2", "src1", "CCTV-2", "http://live.com/2.m3u8")
        };
        var library = new FakeLibraryService(new[] { config });
        var playlist = new FakePlaylistService(channels);
        var epg = new FakeEpgService();
        var player = new FakePlayer();

        var vm = new MainWindowViewModel(playlist, library, epg, player);
        await vm.InitializeAsync();

        Assert.Single(vm.ConfigsVm.Configs);
        Assert.Equal("默认测试源", vm.ConfigsVm.Configs[0].Name);
        Assert.Equal(2, vm.ChannelsVm.AllChannels.Count);
        // Autoplay check: Player must remain in Idle state!
        Assert.Equal(PlayerState.Idle, player.State);
        Assert.Null(player.CurrentChannel);
    }
}
