using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Iptv.Core.Models;
using Iptv.Desktop.Services;
using Iptv.Desktop.ViewModels;
using Xunit;

namespace Iptv.Desktop.Tests.ViewModels;

public class PlayerViewModelTests
{
    private readonly Channel _sampleChannel = new("ch1", "src1", "CCTV-1", "http://live.com/cctv1.m3u8");

    [Fact]
    public async Task NativeSession_PlayRejectionLeavesLoadingAndReportsError()
    {
        using var player = new LibVlcPlayerSession(_ => false);
        string? error = null;
        player.ErrorOccurred += (_, message) => error = message;
        await player.PlayAsync(_sampleChannel);
        Assert.Equal(PlayerState.Error, player.State);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Equal(error, player.ErrorMessage);
    }

    [Fact]
    public async Task NativeSession_NewPlaybackClearsPreviousErrorBeforeLoading()
    {
        var attempt = 0;
        using var player = new LibVlcPlayerSession(_ => ++attempt == 1
            ? throw new InvalidOperationException("failed first attempt") : true);
        await player.PlayAsync(_sampleChannel);
        Assert.NotNull(player.ErrorMessage);
        var loadingNotified = false;
        string? loadingError = "not observed";
        player.StateChanged += (_, state) =>
        {
            if (state == PlayerState.Loading)
            {
                loadingNotified = true;
                loadingError = player.ErrorMessage;
            }
        };
        await player.PlayAsync(_sampleChannel);
        Assert.True(loadingNotified);
        Assert.Null(loadingError);
        Assert.Equal(PlayerState.Loading, player.State);
        Assert.Null(player.ErrorMessage);
    }

    [Fact]
    public async Task ExternalFirstPlayback_NotifiesTitleAndRawUrl()
    {
        var player = new FakePlayer();
        var vm = new PlayerViewModel(player);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        await player.PlayAsync(_sampleChannel);
        Assert.Contains(nameof(PlayerViewModel.ChannelTitle), changes);
        Assert.Contains(nameof(PlayerViewModel.StreamUrl), changes);
        Assert.Equal(_sampleChannel.Name, vm.ChannelTitle);
    }

    [Fact]
    public async Task ExternalSwitchAfterDirectPlay_DoesNotKeepCachedTitle()
    {
        var player = new FakePlayer();
        var vm = new PlayerViewModel(player);
        await vm.PlayCommand.ExecuteAsync(_sampleChannel);
        var next = new Channel("ch2", "src1", "Next channel", "http://live.com/next.m3u8");
        await player.PlayAsync(next);
        Assert.Equal(next.Name, vm.ChannelTitle);
    }

    [Fact]
    public async Task WorkerPlayback_QueuesNotificationsUntilDispatchAndDisposeDropsPending()
    {
        using var player = new LibVlcPlayerSession(_ => false);
        var queue = new Queue<Action>();
        using var vm = new PlayerViewModel(player, action => queue.Enqueue(action));
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        await Task.Run(() => player.PlayAsync(_sampleChannel));
        Assert.Empty(changes);
        Assert.NotEmpty(queue);
        queue.Dequeue()();
        Assert.Contains(nameof(PlayerViewModel.ChannelTitle), changes);
        changes.Clear();
        vm.Dispose();
        while (queue.TryDequeue(out var notification)) notification();
        Assert.Empty(changes);
        await player.PlayAsync(_sampleChannel);
        Assert.Empty(queue);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task DisposingViewModel_DoesNotDisposeSharedPlayer()
    {
        using var player = new LibVlcPlayerSession(_ => true);
        var vm = new PlayerViewModel(player);
        vm.Dispose();
        vm.Dispose();
        await player.PlayAsync(_sampleChannel);
        Assert.Equal(PlayerState.Loading, player.State);
    }

    [Fact]
    public async Task DisposedNativeSession_DoesNotStartPlayback()
    {
        var starts = 0;
        var player = new LibVlcPlayerSession(_ => { starts++; return true; });
        player.Dispose();
        player.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => player.PlayAsync(_sampleChannel));
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task CancelledPlayback_DoesNotChangeSessionOrStartNativePlayback()
    {
        var starts = 0;
        using var player = new LibVlcPlayerSession(_ => { starts++; return true; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            player.PlayAsync(_sampleChannel, new System.Threading.CancellationToken(true)));
        Assert.Equal(PlayerState.Idle, player.State);
        Assert.Null(player.CurrentChannel);
        Assert.Equal(0, starts);
    }

    [Theory]
    [InlineData("Channels")]
    [InlineData("Favorites")]
    [InlineData("Search")]
    public async Task ExternalEntry_NotifiesTitleAndUrlAfterDirectPlay(string entry)
    {
        var player = new FakePlayer();
        var vm = new PlayerViewModel(player);
        await vm.PlayCommand.ExecuteAsync(_sampleChannel);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var next = new Channel("ch2", "src1", "Next channel", "http://live.com/next.m3u8");
        var library = new FakeLibraryService();
        var command = entry switch
        {
            "Channels" => new ChannelsViewModel(new FakePlaylistService(new List<Channel>()), library, new FakeEpgService(), player).PlayChannelCommand,
            "Favorites" => new FavoritesViewModel(library, player).PlayChannelCommand,
            _ => new SearchViewModel(library, player).PlayChannelCommand
        };

        await command.ExecuteAsync(next);

        Assert.Contains(nameof(PlayerViewModel.ChannelTitle), changes);
        Assert.Contains(nameof(PlayerViewModel.StreamUrl), changes);
        Assert.Equal(next.Name, vm.ChannelTitle);
        Assert.Equal(next.StreamUrl, vm.StreamUrl);
    }

    [Fact]
    public void InitialState_IsIdleAndZeroPosition()
    {
        var player = new FakePlayer();
        var vm = new PlayerViewModel(player);

        Assert.Equal(PlayerState.Idle, vm.State);
        Assert.Null(vm.CurrentChannel);
        Assert.Equal(100, vm.Volume);
        Assert.False(vm.IsMuted);
        Assert.False(vm.IsPlaying);
        Assert.False(vm.IsFullScreen);
    }

    [Fact]
    public async Task PlayChannel_UpdatesStateAndCurrentChannel()
    {
        var player = new FakePlayer();
        var vm = new PlayerViewModel(player);

        await vm.PlayCommand.ExecuteAsync(_sampleChannel);

        Assert.Equal(PlayerState.Playing, vm.State);
        Assert.True(vm.IsPlaying);
        Assert.Equal(_sampleChannel, vm.CurrentChannel);
        Assert.Equal("CCTV-1", vm.ChannelTitle);
    }

    [Fact]
    public async Task PlayChannel_WithSensitiveStreamUrl_ExposesSafeDisplayStreamUrlWhileRetainingRaw()
    {
        var sensitiveChannel = new Channel("ch_sens", "src1", "加密频道", "http://live.example.com/live.m3u8?token=secret_token_123&password=pass");
        var player = new FakePlayer();
        var vm = new PlayerViewModel(player);

        await vm.PlayCommand.ExecuteAsync(sensitiveChannel);

        // Raw StreamUrl retained for native player
        Assert.Equal("http://live.example.com/live.m3u8?token=secret_token_123&password=pass", vm.StreamUrl);
        // UI displays only the origin; native playback retains the full URL.
        Assert.Equal("http://live.example.com", vm.SafeDisplayStreamUrl);
    }

    [Fact]
    public async Task TogglePlayPause_TogglesBetweenPlayingAndPaused()
    {
        var player = new FakePlayer();
        var vm = new PlayerViewModel(player);
        await vm.PlayCommand.ExecuteAsync(_sampleChannel);

        await vm.TogglePlayPauseCommand.ExecuteAsync(null);
        Assert.Equal(PlayerState.Paused, vm.State);
        Assert.False(vm.IsPlaying);

        await vm.TogglePlayPauseCommand.ExecuteAsync(null);
        Assert.Equal(PlayerState.Playing, vm.State);
        Assert.True(vm.IsPlaying);
    }

    [Fact]
    public async Task Stop_StopsPlaybackAndClearsChannel()
    {
        var player = new FakePlayer();
        var vm = new PlayerViewModel(player);
        await vm.PlayCommand.ExecuteAsync(_sampleChannel);

        await vm.StopCommand.ExecuteAsync(null);

        Assert.Equal(PlayerState.Stopped, vm.State);
        Assert.False(vm.IsPlaying);
    }

    [Fact]
    public async Task VolumeChange_ClampedBetweenZeroAndHundred()
    {
        var player = new FakePlayer();
        var vm = new PlayerViewModel(player);

        await vm.SetVolumeCommand.ExecuteAsync(80);
        Assert.Equal(80, vm.Volume);
        Assert.Equal(80, player.Volume);

        await vm.SetVolumeCommand.ExecuteAsync(150);
        Assert.Equal(100, vm.Volume);

        await vm.SetVolumeCommand.ExecuteAsync(-20);
        Assert.Equal(0, vm.Volume);
    }

    [Fact]
    public void ToggleMute_TogglesMuteState()
    {
        var player = new FakePlayer();
        var vm = new PlayerViewModel(player);

        vm.ToggleMuteCommand.Execute(null);
        Assert.True(vm.IsMuted);
        Assert.True(player.IsMuted);

        vm.ToggleMuteCommand.Execute(null);
        Assert.False(vm.IsMuted);
        Assert.False(player.IsMuted);
    }

    [Fact]
    public void ToggleFullScreen_TogglesFullScreenState()
    {
        var player = new FakePlayer();
        var vm = new PlayerViewModel(player);

        Assert.False(vm.IsFullScreen);
        vm.ToggleFullScreenCommand.Execute(null);
        Assert.True(vm.IsFullScreen);
        vm.ToggleFullScreenCommand.Execute(null);
        Assert.False(vm.IsFullScreen);
    }
}
