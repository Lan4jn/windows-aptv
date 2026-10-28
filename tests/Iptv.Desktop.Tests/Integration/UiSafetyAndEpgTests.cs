using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Iptv.Core.Models;
using Iptv.Desktop;
using Iptv.Desktop.ViewModels;

namespace Iptv.Desktop.Tests.Integration;

public class UiSafetyAndEpgTests
{
    [AvaloniaTheory]
    [InlineData("http://SECRETUSER:SECRETPASS@example.invalid:8080/live/SECRETUSER/SECRETPASS/1.ts?unknown=SECRETTOKEN", "http://example.invalid:8080")]
    [InlineData("https://example.invalid/SECRET/path?username=SECRET&custom=SECRETTOKEN#SECRET", "https://example.invalid")]
    [InlineData("invalid SECRET?custom=SECRETTOKEN", "地址已隐藏")]
    [InlineData("file:///SECRET/list.m3u", "地址已隐藏")]
    [InlineData("https://user:SECRET@[::1]:8443/live/SECRET?custom=SECRET", "https://[::1]:8443")]
    public async Task ActualUrlControls_HideAllCredentials_WithoutChangingPlayback(string url, string expected)
    {
        var config = new SourceConfig("source", "Source", SourceKind.M3u, url);
        var channel = new Channel("one", "source", "Channel", url);
        var player = new FakePlayer();
        var vm = new MainWindowViewModel(new FakePlaylistService(new[] { channel }),
            new FakeLibraryService(new[] { config }), new FakeEpgService(), player);
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            await window.InitializationTask;
            window.UpdateLayout();
            var card = window.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => ReferenceEquals(t.DataContext, channel) && t.FontSize == 10);
            Assert.Equal(expected, card.Text);
            Assert.Equal(expected, window.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.DataContext is SourceConfigItemViewModel && t.FontSize == 10).Text);
            await vm.ChannelsVm.PlayChannelCommand.ExecuteAsync(channel);
            Assert.Equal(expected, window.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => ReferenceEquals(t.DataContext, vm.PlayerVm) && t.FontSize == 10 && t.Foreground?.ToString() == "#ff666666").Text);
            Assert.Equal(url, player.CurrentChannel?.StreamUrl);
            vm.NavigateToConfigsCommand.Execute(null);
            window.UpdateLayout();
            Assert.Equal(expected, window.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.DataContext is SourceConfigItemViewModel && t.FontSize == 11).Text);
            Assert.All(window.GetVisualDescendants().OfType<TextBlock>(), t => Assert.DoesNotContain("SECRET", t.Text ?? ""));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProgrammeFailure_ShowsSafeStatusWhileChannelsRemainVisible()
    {
        var config = new SourceConfig("source", "Source", SourceKind.M3u, "https://example.invalid/list", customEpgUrl: "https://example.invalid/epg");
        var channel = new Channel("one", "source", "Channel", "https://example.invalid/live");
        var epg = new FakeEpgService { LoadHandler = (url, _) => url is null ? Task.CompletedTask : Task.FromException(new Exception("SECRET")) };
        var vm = new MainWindowViewModel(new FakePlaylistService(new[] { channel }), new FakeLibraryService(new[] { config }), epg, new FakePlayer());
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            await window.InitializationTask;
            window.UpdateLayout();
            Assert.Single(vm.ChannelsVm.FilteredChannels);
            var status = window.FindControl<TextBlock>("ChannelsStatus")!;
            Assert.Contains("节目", status.Text);
            Assert.Contains("失败", status.Text);
            Assert.DoesNotContain("SECRET", status.Text);
            Assert.True(status.IsEffectivelyVisible);
            Assert.Equal("暂无当前节目", window.FindControl<TextBlock>("CurrentProgrammeTitle")?.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingWindow_DisposesProgrammeOwnerAndPlayerSubscriptions()
    {
        var player = new FakePlayer();
        var vm = new MainWindowViewModel(new FakePlaylistService(), new FakeLibraryService(), new FakeEpgService(), player);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await window.InitializationTask;
        window.Close();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => vm.ChannelsVm.LoadChannelsForConfigAsync(
            new SourceConfig("source", "Source", SourceKind.M3u, "https://example.invalid/list")));
        var notifications = 0;
        vm.PlayerVm.PropertyChanged += (_, _) => notifications++;
        player.TriggerError("post-close");
        Assert.Equal(0, notifications);
    }

    [AvaloniaFact]
    public async Task ProgrammeControls_ShowTitlesTimes_AndClearWhenSourceChanges()
    {
        var start = DateTimeOffset.Now.AddMinutes(-10);
        var now = new EpgProgramme("epg", "Current show", start, start.AddMinutes(30));
        var next = new EpgProgramme("epg", "Next show", start.AddMinutes(30), start.AddMinutes(60));
        var channel = new Channel("one", "source", "Channel", "https://example.invalid/live", tvgId: "epg");
        var config = new SourceConfig("source", "Source", SourceKind.M3u, "https://example.invalid/list");
        var vm = new MainWindowViewModel(new FakePlaylistService(new[] { channel }),
            new FakeLibraryService(new[] { config }), new FakeEpgService(now, next), new FakePlayer());
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            await window.InitializationTask;
            await vm.ChannelsVm.PlayChannelCommand.ExecuteAsync(channel);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Equal("Current show", window.FindControl<TextBlock>("CurrentProgrammeTitle")?.Text);
            Assert.Equal("Next show", window.FindControl<TextBlock>("NextProgrammeTitle")?.Text);
            Assert.Equal(vm.ChannelsVm.NextProgrammeTime, window.FindControl<TextBlock>("NextProgrammeTime")?.Text);
            Assert.Equal(vm.ChannelsVm.CurrentProgrammeTime, window.FindControl<TextBlock>("CurrentProgrammeTime")?.Text);
            Assert.False(string.IsNullOrEmpty(window.FindControl<TextBlock>("CurrentProgrammeTime")?.Text));
            await vm.ChannelsVm.LoadChannelsForConfigAsync(new SourceConfig("second", "Second", SourceKind.M3u, "https://example.invalid/second"));
            Assert.Equal("暂无当前节目", window.FindControl<TextBlock>("CurrentProgrammeTitle")?.Text);
            Assert.Equal("暂无下一节目", window.FindControl<TextBlock>("NextProgrammeTitle")?.Text);
            Assert.Equal("", window.FindControl<TextBlock>("CurrentProgrammeTime")?.Text);
        }
        finally { window.Close(); }
    }
}
