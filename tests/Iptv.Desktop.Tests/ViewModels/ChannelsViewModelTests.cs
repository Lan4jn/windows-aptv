using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Net;
using System.Net.Http;
using Iptv.Core.Models;
using Iptv.Desktop.Services;
using Iptv.Desktop.ViewModels;
using Xunit;

namespace Iptv.Desktop.Tests.ViewModels;

public class ChannelsViewModelTests
{
    private readonly SourceConfig _testConfig = new("src1", "测试源", SourceKind.M3u, "http://test.com/list.m3u");
    private readonly List<Channel> _testChannels = new()
    {
        new("ch1", "src1", "CCTV-1 综合", "http://live.com/cctv1.m3u8", group: "央视", tvgId: "cctv1"),
        new("ch2", "src1", "CCTV-2 财经", "http://live.com/cctv2.m3u8", group: "央视", tvgId: "cctv2"),
        new("ch3", "src1", "湖南卫视", "http://live.com/hunan.m3u8", group: "卫视", tvgId: "hunan"),
        new("ch4", "src1", "浙江卫视", "http://live.com/zhejiang.m3u8", group: "卫视", tvgId: "zhejiang"),
    };

    [Fact]
    public async Task LoadChannelsAsync_PopulatesChannelsAndGroups()
    {
        var playlistService = new FakePlaylistService(_testChannels);
        var libraryService = new FakeLibraryService();
        var epgService = new FakeEpgService();
        var player = new FakePlayer();

        using var vm = new ChannelsViewModel(playlistService, libraryService, epgService, player);
        await vm.LoadChannelsForConfigAsync(_testConfig);

        Assert.Equal(4, vm.AllChannels.Count);
        Assert.Equal(4, vm.FilteredChannels.Count);
        // Groups: "全部", "央视", "卫视"
        Assert.Equal(3, vm.Groups.Count);
        Assert.Equal("全部", vm.SelectedGroup);
    }

    [Fact]
    public async Task FilterByGroup_FiltersChannelList()
    {
        var playlistService = new FakePlaylistService(_testChannels);
        var libraryService = new FakeLibraryService();
        var epgService = new FakeEpgService();
        var player = new FakePlayer();

        using var vm = new ChannelsViewModel(playlistService, libraryService, epgService, player);
        await vm.LoadChannelsForConfigAsync(_testConfig);

        vm.SelectedGroup = "央视";

        Assert.Equal(2, vm.FilteredChannels.Count);
        Assert.All(vm.FilteredChannels, ch => Assert.Equal("央视", ch.Group));
    }

    [Theory]
    [InlineData("cctv", 2)]
    [InlineData("CCTV", 2)]
    [InlineData("综合", 1)]
    [InlineData("卫视", 2)]
    [InlineData("NotExist", 0)]
    public async Task FilterBySearchText_CaseInsensitiveSearch(string query, int expectedCount)
    {
        var playlistService = new FakePlaylistService(_testChannels);
        var libraryService = new FakeLibraryService();
        var epgService = new FakeEpgService();
        var player = new FakePlayer();

        using var vm = new ChannelsViewModel(playlistService, libraryService, epgService, player);
        await vm.LoadChannelsForConfigAsync(_testConfig);

        vm.SearchText = query;

        Assert.Equal(expectedCount, vm.FilteredChannels.Count);
    }

    [Fact]
    public async Task PlayChannel_DelegatesToPlayer()
    {
        var playlistService = new FakePlaylistService(_testChannels);
        var libraryService = new FakeLibraryService();
        var epgService = new FakeEpgService();
        var player = new FakePlayer();

        using var vm = new ChannelsViewModel(playlistService, libraryService, epgService, player);
        await vm.LoadChannelsForConfigAsync(_testConfig);

        var targetChannel = _testChannels[0];
        await vm.PlayChannelCommand.ExecuteAsync(targetChannel);

        Assert.Equal(targetChannel, player.CurrentChannel);
        Assert.Equal(PlayerState.Playing, player.State);
    }

    [Fact]
    public async Task ToggleFavorite_AddsAndRemovesFavorite()
    {
        var playlistService = new FakePlaylistService(_testChannels);
        var libraryService = new FakeLibraryService();
        var epgService = new FakeEpgService();
        var player = new FakePlayer();

        using var vm = new ChannelsViewModel(playlistService, libraryService, epgService, player);
        await vm.LoadChannelsForConfigAsync(_testConfig);

        var targetChannel = _testChannels[0];
        Assert.False(await libraryService.IsFavoriteAsync(targetChannel.Id));

        await vm.ToggleFavoriteCommand.ExecuteAsync(targetChannel);
        Assert.True(await libraryService.IsFavoriteAsync(targetChannel.Id));

        await vm.ToggleFavoriteCommand.ExecuteAsync(targetChannel);
        Assert.False(await libraryService.IsFavoriteAsync(targetChannel.Id));
    }

    [Fact]
    public async Task SelectChannel_UpdatesEpgNowNext()
    {
        var playlistService = new FakePlaylistService(_testChannels);
        var libraryService = new FakeLibraryService();
        var nowProg = new EpgProgramme("cctv1", "新闻联播", DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(20));
        var nextProg = new EpgProgramme("cctv1", "焦点访谈", DateTimeOffset.UtcNow.AddMinutes(20), DateTimeOffset.UtcNow.AddMinutes(40));
        var epgService = new FakeEpgService(nowProg, nextProg);
        var player = new FakePlayer();

        using var vm = new ChannelsViewModel(playlistService, libraryService, epgService, player);
        await vm.LoadChannelsForConfigAsync(_testConfig);

        vm.SelectedChannel = _testChannels[0];
        await Task.Delay(50); // wait async epg fetch

        Assert.Equal("新闻联播", vm.CurrentProgrammeTitle);
        Assert.Equal("焦点访谈", vm.NextProgrammeTitle);
    }

    [Fact]
    public async Task LoadChannelsAsync_CallsEnsureGuideLoaded_WithCustomOverrideFirst()
    {
        var epgUrls = new[] { "http://m3u.com/header_epg.xml" };
        var playlistService = new FakePlaylistService(_testChannels, epgUrls);
        var libraryService = new FakeLibraryService();
        var customConfig = new SourceConfig("src_custom", "自定义", SourceKind.M3u, "http://test.com/list.m3u", customEpgUrl: "http://override.com/custom_epg.xml");
        var epgService = new FakeEpgService();
        var player = new FakePlayer();

        using var vm = new ChannelsViewModel(playlistService, libraryService, epgService, player);
        await vm.LoadChannelsForConfigAsync(customConfig);

        Assert.Equal(4, vm.AllChannels.Count);
        Assert.NotNull(vm.CurrentConfig);
        Assert.Equal("http://override.com/custom_epg.xml", vm.CurrentConfig.CustomEpgUrl);
    }

    [Fact]
    public async Task LoadChannelsAsync_FallsBackToGlobalDefaultEpgUrl_WhenNoCustomOrM3uEpg()
    {
        var playlistService = new FakePlaylistService(_testChannels, Array.Empty<string>());
        var libraryService = new FakeLibraryService();
        var simpleConfig = new SourceConfig("src_simple", "普通源", SourceKind.M3u, "http://test.com/list.m3u");
        var epgService = new FakeEpgService();
        var player = new FakePlayer();

        string globalEpg = "http://global.com/default_epg.xml";
        using var vm = new ChannelsViewModel(playlistService, libraryService, epgService, player, () => globalEpg);
        await vm.LoadChannelsForConfigAsync(simpleConfig);

        Assert.Equal(4, vm.AllChannels.Count);
    }
    [Fact]
    public async Task EpgFailure_DoesNotDiscardPlayableChannels()
    {
        var epg = new FakeEpgService { LoadHandler = (url, ct) =>
            url == null ? Task.CompletedTask : Task.FromException(new InvalidOperationException("secret")) };
        using var vm = new ChannelsViewModel(new FakePlaylistService(_testChannels, new[] { "http://epg/guide" }), new FakeLibraryService(), epg, new FakePlayer());
        await vm.LoadChannelsForConfigAsync(_testConfig);
        Assert.Equal(4, vm.FilteredChannels.Count);
        Assert.Contains("节目", vm.StatusMessage);
        Assert.DoesNotContain("secret", vm.StatusMessage);
    }

    [Fact]
    public async Task NoEpgSource_ClearsPreviousGuideAndSelection()
    {
        var epg = new FakeEpgService();
        using var vm = new ChannelsViewModel(new FakePlaylistService(_testChannels), new FakeLibraryService(), epg, new FakePlayer());
        await vm.LoadChannelsForConfigAsync(_testConfig);
        vm.SelectedChannel = _testChannels[0];
        await vm.LoadChannelsForConfigAsync(_testConfig);
        Assert.Null(vm.SelectedChannel);
        Assert.Contains((string?)null, epg.LoadedUrls);
    }
    [Fact]
    public async Task RefreshCommand_BypassesGuideCache()
    {
        var epg = new FakeEpgService();
        using var vm = new ChannelsViewModel(new FakePlaylistService(_testChannels, new[] { "http://epg/guide" }), new FakeLibraryService(), epg, new FakePlayer());
        await vm.LoadChannelsForConfigAsync(_testConfig);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(epg.RefreshFlags[^1]);
    }

    [Fact]
    public async Task LateSelectionResult_CannotOverwriteNewChannelOrClearedSource()
    {
        var pending = new TaskCompletionSource<(EpgProgramme?, EpgProgramme?)>();
        var epg = new FakeEpgService { NowNextHandler = (channel, time, ct) => channel.Id == "ch1" ? pending.Task : Task.FromResult<(EpgProgramme?, EpgProgramme?)>((null, null)) };
        using var vm = new ChannelsViewModel(new FakePlaylistService(_testChannels), new FakeLibraryService(), epg, new FakePlayer());
        await vm.LoadChannelsForConfigAsync(_testConfig);
        vm.SelectedChannel = _testChannels[0];
        vm.SelectedChannel = _testChannels[1];
        var prog = new EpgProgramme("cctv1", "stale", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        pending.SetResult((prog, null));
        Assert.Null(vm.CurrentProgrammeTitle);
        vm.ClearChannels();
        await vm.LoadChannelsForConfigAsync(_testConfig);
        Assert.Null(vm.CurrentProgrammeTitle);
        Assert.Null(vm.CurrentProgrammeTime);
    }

    [Fact]
    public async Task SupersededPlaylist_CannotReplaceNewSource()
    {
        var pending = new TaskCompletionSource<PlaylistChannelsResult>();
        CancellationToken oldToken = default;
        var playlist = new FakePlaylistService { LoadHandler = (config, ct) => {
            if (config.Id == "src1") { oldToken = ct; return pending.Task; }
            return Task.FromResult(new PlaylistChannelsResult(new[] { _testChannels[1] }, Array.Empty<string>(), Array.Empty<string>()));
        }};
        using var vm = new ChannelsViewModel(playlist, new FakeLibraryService(), new FakeEpgService(), new FakePlayer());
        var old = vm.LoadChannelsForConfigAsync(_testConfig);
        await vm.LoadChannelsForConfigAsync(new SourceConfig("new", "new", SourceKind.M3u, "http://new/list"));
        Assert.True(oldToken.IsCancellationRequested);
        pending.SetResult(new PlaylistChannelsResult(_testChannels, Array.Empty<string>(), Array.Empty<string>()));
        await old;
        Assert.Single(vm.FilteredChannels);
        Assert.Equal("new", vm.CurrentConfig?.Id);
    }

    [Fact]
    public async Task ClockTick_UpdatesProgrammeAtBoundary_AndDisposeStopsTimer()
    {
        var clock = new ManualClock();
        var start = clock.GetUtcNow();
        var first = new EpgProgramme("cctv1", "first", start, start.AddMinutes(1));
        var second = new EpgProgramme("cctv1", "second", start.AddMinutes(1), start.AddMinutes(2));
        int queries = 0;
        var epg = new FakeEpgService { NowNextHandler = (channel, time, ct) => {
            queries++;
            return Task.FromResult<(EpgProgramme?, EpgProgramme?)>(time < first.End ? (first, second) : (second, null));
        }};
        using var vm = new ChannelsViewModel(new FakePlaylistService(_testChannels), new FakeLibraryService(), epg, new FakePlayer(), timeProvider: clock);
        await vm.LoadChannelsForConfigAsync(_testConfig);
        vm.SelectedChannel = _testChannels[0];
        Assert.Equal("first", vm.CurrentProgrammeTitle);
        Assert.NotNull(vm.CurrentProgrammeTime);
        Assert.Equal("second", vm.NextProgrammeTitle);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("second", vm.CurrentProgrammeTitle);
        Assert.Null(vm.NextProgrammeTitle);
        Assert.Null(vm.NextProgrammeTime);
        vm.Dispose();
        var count = queries;
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(count, queries);
        Assert.True(clock.TimerDisposed);
    }

    [Fact]
    public async Task CoreService_ClearAndSupersede_PreventStaleGuidePublication()
    {
        var oldResponse = new TaskCompletionSource<HttpResponseMessage>();
        using var client = new HttpClient(new ControlledHandler((request, ct) =>
            request.RequestUri!.AbsolutePath == "/old" ? oldResponse.Task : Task.FromResult(GuideResponse("new"))));
        var service = new CoreEpgService(client);
        var old = service.EnsureGuideLoadedAsync("http://guide/old");
        await service.EnsureGuideLoadedAsync("http://guide/new");
        oldResponse.SetResult(GuideResponse("old"));
        await old;
        var time = DateTimeOffset.Parse("2026-04-01T00:30:00Z");
        Assert.Equal("new", (await service.GetNowNextAsync(_testChannels[0], time)).Now?.Title);
        await service.EnsureGuideLoadedAsync(null);
        Assert.Null((await service.GetNowNextAsync(_testChannels[0], time)).Now);
    }

    [Fact]
    public async Task CoreService_ManualRefreshFetchesNewGuide_AndFailureClearsOldGuide()
    {
        int requestCount = 0;
        using var client = new HttpClient(new ControlledHandler((request, ct) => {
            requestCount++;
            return Task.FromResult(requestCount == 3 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : GuideResponse($"guide-{requestCount}"));
        }));
        var service = new CoreEpgService(client);
        var time = DateTimeOffset.Parse("2026-04-01T00:30:00Z");
        await service.EnsureGuideLoadedAsync("http://guide/xml");
        await service.EnsureGuideLoadedAsync("http://guide/xml", forceRefresh: true);
        Assert.Equal("guide-2", (await service.GetNowNextAsync(_testChannels[0], time)).Now?.Title);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.EnsureGuideLoadedAsync("http://guide/xml", forceRefresh: true));
        Assert.DoesNotContain("http://guide", error.ToString());
        Assert.Null((await service.GetNowNextAsync(_testChannels[0], time)).Now);
    }

    [Fact]
    public async Task CoreService_CancelledDownloadCannotPublishOrCacheGuide()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        using var client = new HttpClient(new ControlledHandler((request, ct) => pending.Task));
        var service = new CoreEpgService(client);
        using var cancellation = new CancellationTokenSource();
        var load = service.EnsureGuideLoadedAsync("http://guide/xml", cancellation.Token);
        cancellation.Cancel();
        pending.SetResult(GuideResponse("cancelled"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.Null((await service.GetNowNextAsync(_testChannels[0], DateTimeOffset.Parse("2026-04-01T00:30:00Z"))).Now);
    }

    [Fact]
    public async Task CoreService_ClearWhileDownloading_CannotRestoreOldGuide()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        using var client = new HttpClient(new ControlledHandler((request, ct) => pending.Task));
        var service = new CoreEpgService(client);
        var load = service.EnsureGuideLoadedAsync("http://guide/xml");
        await service.EnsureGuideLoadedAsync(null);
        pending.SetResult(GuideResponse("old"));
        await load;
        Assert.Null((await service.GetNowNextAsync(_testChannels[0], DateTimeOffset.Parse("2026-04-01T00:30:00Z"))).Now);
    }

    [Fact]
    public async Task Dispose_CancelsPendingLoadWithoutPublishingChannels()
    {
        var pending = new TaskCompletionSource<PlaylistChannelsResult>();
        CancellationToken token = default;
        var playlist = new FakePlaylistService { LoadHandler = (config, ct) => { token = ct; return pending.Task; } };
        using var vm = new ChannelsViewModel(playlist, new FakeLibraryService(), new FakeEpgService(), new FakePlayer());
        var load = vm.LoadChannelsForConfigAsync(_testConfig);
        vm.Dispose();
        Assert.True(token.IsCancellationRequested);
        pending.SetResult(new PlaylistChannelsResult(_testChannels, Array.Empty<string>(), Array.Empty<string>()));
        await load;
        Assert.Empty(vm.FilteredChannels);
        Assert.Null(vm.CurrentProgrammeTitle);
    }

    private static HttpResponseMessage GuideResponse(string title) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"<tv><channel id='cctv1'><display-name>CCTV-1 综合</display-name></channel><programme channel='cctv1' start='20260401000000 Z' stop='20260401010000 Z'><title>{title}</title></programme></tv>")
    };

    private sealed class ControlledHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        private ManualTimer? _timer;
        public bool TimerDisposed => _timer?.Disposed == true;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) { _now += amount; _timer?.Tick(); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => _timer = new ManualTimer(callback, state);
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed { get; private set; }
            public void Tick() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
