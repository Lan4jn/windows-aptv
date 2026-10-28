using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Iptv.Core.Models;
using Iptv.Desktop;
using Iptv.Desktop.Services;
using Iptv.Desktop.ViewModels;

[assembly: AvaloniaTestApplication(typeof(Iptv.Desktop.Tests.Integration.HeadlessApplication))]

namespace Iptv.Desktop.Tests.Integration;

public sealed class HeadlessApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessApplication>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class HeadlessWindowRegressionTests
{
    [AvaloniaFact]
    public async Task ShowingWindow_InitializesRealViewModel_WithoutAutoplay()
    {
        var config = new SourceConfig("source", "Saved Source", SourceKind.M3u, "https://example.invalid/source.m3u");
        var channel = new Channel("one", "source", "Channel", "https://example.invalid/live.ts");
        var player = new FakePlayer();
        var vm = new MainWindowViewModel(new FakePlaylistService(new[] { channel }),
            new FakeLibraryService(new[] { config }), new FakeEpgService(), player);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.Single(vm.ConfigsVm.Configs);
        Assert.Single(vm.ChannelsVm.AllChannels);
        Assert.Equal(PlayerState.Idle, player.State);
        window.Close();
    }

    [AvaloniaFact]
    public void LocalSourceForm_HasFilePickerButton()
    {
        var window = CreateWindow();
        Assert.NotNull(window.FindControl<Button>("BrowseM3uButton"));
        window.Close();
    }

    [AvaloniaFact]
    public void Fullscreen_ExitRestoresPreviousWindowState()
    {
        var window = CreateWindow();
        var vm = (MainWindowViewModel)window.DataContext!;
        window.Show();
        window.WindowState = WindowState.Maximized;
        vm.PlayerVm.IsFullScreen = true;
        Assert.Equal(WindowState.FullScreen, window.WindowState);
        vm.PlayerVm.IsFullScreen = false;
        Assert.Equal(WindowState.Maximized, window.WindowState);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DeleteCommand_RequiresExplicitConfirmation()
    {
        var config = new SourceConfig("source", "Saved", SourceKind.M3u, "https://example.invalid/source.m3u");
        var library = new FakeLibraryService(new[] { config });
        var vm = new ConfigsViewModel(library);
        await vm.InitializeAsync();
        await vm.DeleteConfigCommand.ExecuteAsync(vm.Configs.Single());
        Assert.Single(await library.GetConfigsAsync());
    }

    [AvaloniaFact]
    public void Navigation_OnlyShowsSelectedSection()
    {
        var window = CreateWindow();
        var vm = (MainWindowViewModel)window.DataContext!;
        window.Show();
        vm.NavigateToConfigsCommand.Execute(null);
        var channels = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<Grid>().First(g => g.DataContext is ChannelsViewModel);
        var configs = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<Grid>().First(g => g.DataContext is ConfigsViewModel);
        Assert.False(channels.IsVisible);
        Assert.True(configs.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SourcePicker_HasTwoWaySelectionBinding()
    {
        var sources = new[]
        {
            new SourceConfig("first", "First", SourceKind.M3u, "https://example.invalid/first.m3u"),
            new SourceConfig("second", "Second", SourceKind.M3u, "https://example.invalid/second.m3u")
        };
        var vm = new MainWindowViewModel(new FakePlaylistService(), new FakeLibraryService(sources),
            new FakeEpgService(), new FakePlayer());
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await window.InitializationTask;
        var picker = window.FindControl<ComboBox>("SourcePicker");
        Assert.NotNull(picker);
        picker.SelectedItem = vm.ConfigsVm.Configs.Single(c => c.Id == "second");
        await vm.ConfigsVm.SelectionLoadTask;
        Assert.Equal("second", vm.ConfigsVm.SelectedConfig?.Id);
        Assert.Equal("second", vm.ChannelsVm.CurrentConfig?.Id);
        await vm.ConfigsVm.RefreshConfigsAsync();
        Assert.Equal("second", vm.ConfigsVm.SelectedConfig?.Id);
        Assert.Equal("second", vm.ChannelsVm.CurrentConfig?.Id);
        window.Close();
    }

    [AvaloniaFact]
    public void UnsupportedAppearanceSettings_CannotPretendToApply()
    {
        var window = CreateWindow();
        var selectors = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window)
            .OfType<ComboBox>().Where(c => c.DataContext is SettingsViewModel).ToArray();
        Assert.Equal(2, selectors.Length);
        Assert.All(selectors, selector => Assert.False(selector.IsEnabled));
        window.Close();
    }

    [AvaloniaFact]
    public void ConfigurationForm_HasScrollableContent()
    {
        var window = CreateWindow();
        Assert.NotNull(window.FindControl<ScrollViewer>("AddConfigScroll"));
        window.Close();
    }

    private static MainWindow CreateWindow() => new()
    {
        DataContext = new MainWindowViewModel(new FakePlaylistService(), new FakeLibraryService(),
            new FakeEpgService(), new FakePlayer())
    };
}
