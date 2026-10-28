using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Iptv.Core.Models;
using Iptv.Desktop.Services;
using Iptv.Desktop.Tests.Services;
using Avalonia.Headless.XUnit;
using Iptv.Desktop;
using Iptv.Desktop.ViewModels;

namespace Iptv.Desktop.Tests.Integration;

public class LocalPlaybackUiTests
{
    [AvaloniaTheory]
    [InlineData(850)]
    [InlineData(1280)]
    public async Task ActualLocalButton_UsesPickerAndExistingSession_WithoutLibraryPersistence(int width)
    {
        using var fixture = new LocalMediaTests.LocalFixture();
        var player = new FakePlayer();
        var library = new FakeLibraryService();
        var vm = new MainWindowViewModel(new FakePlaylistService(), library, new FakeEpgService(), player)
        { PickLocalMediaAsync = () => Task.FromResult<string?>(fixture.FilePath) };
        var window = new MainWindow { DataContext = vm, Width = width, Height = 600 };
        try
        {
            window.Show();
            await window.InitializationTask;
            var button = window.FindControl<Button>("OpenLocalMediaButton")!;
            Assert.Same(vm.OpenLocalMediaCommand, button.Command);
            await ((AsyncRelayCommand)button.Command!).ExecuteAsync(null);
            window.UpdateLayout();
            Assert.Equal(Path.GetFileName(fixture.FilePath), vm.PlayerVm.ChannelTitle);
            Assert.Equal(fixture.FilePath, new Uri(player.CurrentChannel!.StreamUrl).LocalPath);
            Assert.Equal(PlayerState.Playing, player.State);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == Path.GetFileName(fixture.FilePath));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), text => (text.Text ?? "").Contains(fixture.Root, StringComparison.Ordinal));
            Assert.NotNull(window.FindControl<LibVLCSharp.Avalonia.VideoView>("PlayerVideoView"));
            var point = button.TranslatePoint(default, window)!.Value;
            Assert.True(point.X >= 0 && point.X + button.Bounds.Width <= window.ClientSize.Width && point.Y + button.Bounds.Height <= window.ClientSize.Height);
            await vm.PlayerVm.PauseCommand.ExecuteAsync(null);
            Assert.Equal(PlayerState.Paused, player.State);
            await vm.PlayerVm.StopCommand.ExecuteAsync(null);
            Assert.Equal(PlayerState.Stopped, player.State);
            Assert.Empty(await library.GetConfigsAsync());
            Assert.Empty(await library.GetHistoryAsync());
            Assert.Empty(await library.GetFavoritesAsync());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PickerCancelAndFailure_DoNotChangeCurrentPlayback_AndErrorsAreGeneric()
    {
        using var fixture = new LocalMediaTests.LocalFixture();
        var player = new FakePlayer();
        var vm = new MainWindowViewModel(new FakePlaylistService(), new FakeLibraryService(), new FakeEpgService(), player);
        await vm.OpenLocalMediaAsync(fixture.FilePath);
        var current = player.CurrentChannel;
        vm.ErrorMessage = "Prior status";
        vm.PickLocalMediaAsync = () => Task.FromResult<string?>(null);
        await vm.OpenLocalMediaCommand.ExecuteAsync(null);
        Assert.Same(current, player.CurrentChannel);
        Assert.Equal(PlayerState.Playing, player.State);
        Assert.Equal("Prior status", vm.ErrorMessage);
        vm.PickLocalMediaAsync = () => Task.FromException<string?>(new IOException(fixture.Root));
        await vm.OpenLocalMediaCommand.ExecuteAsync(null);
        Assert.Same(current, player.CurrentChannel);
        Assert.DoesNotContain(fixture.Root, vm.ErrorMessage!);
        await vm.OpenLocalMediaAsync("https://remote.invalid/movie.mp4");
        Assert.Same(current, player.CurrentChannel);
        Assert.DoesNotContain("remote.invalid", vm.ErrorMessage!);
        vm.ChannelsVm.Dispose(); vm.PlayerVm.Dispose(); player.Dispose();
    }

    [AvaloniaFact]
    public async Task LocalEntry_UsesExistingPreflightAndFailedNativeStartShowsError()
    {
        using var fixture = new LocalMediaTests.LocalFixture();
        var library = new FakeLibraryService();
        var starts = 0;
        using var player = new LibVlcPlayerSession(_ => { starts++; return false; }, new CoreProxyPolicyService(library).CheckNativePlaybackAsync);
        var vm = new MainWindowViewModel(new FakePlaylistService(), library, new FakeEpgService(), player);
        await vm.OpenLocalMediaAsync(fixture.FilePath);
        Assert.Equal(1, starts);
        Assert.Equal(PlayerState.Error, player.State);
        Assert.Contains("播放失败", vm.ErrorMessage);
        Assert.DoesNotContain(fixture.Root, vm.ErrorMessage!);
        vm.ChannelsVm.Dispose(); vm.PlayerVm.Dispose();
    }

    [AvaloniaFact]
    public async Task Window_ExposesLocalMediaCommand_WithoutAnySource()
    {
        var vm = new MainWindowViewModel(new FakePlaylistService(), new FakeLibraryService(), new FakeEpgService(), new FakePlayer());
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            await window.InitializationTask;
            var button = window.FindControl<Button>("OpenLocalMediaButton");
            Assert.NotNull(button);
            Assert.NotNull(button.Command);
            Assert.True(button.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }
}
