using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Iptv.Desktop.Services;
using Iptv.Desktop.ViewModels;
using LibVLCSharp.Avalonia;

namespace Iptv.Desktop;

public partial class MainWindow : Window
{
    private WindowState _previousState;
    private Avalonia.PixelPoint _previousPosition;
    private Avalonia.Size _previousSize;
    public System.Threading.Tasks.Task InitializationTask { get; private set; } = System.Threading.Tasks.Task.CompletedTask;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        StartupDiagnostics.Current?.Stage(StartupStage.WindowOpened);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PickLocalMediaAsync ??= PickLocalMediaAsync;
            vm.ConfigsVm.ConfirmDeleteAsync = config => WindowDialogService.ConfirmAsync(this, $"删除配置“{config.Name}”？");
            InitializationTask = InitializeViewModelAsync(vm);
            if (vm.PlayerVm.Session is LibVlcPlayerSession vlcSession && vlcSession.MediaPlayer != null)
            {
                var videoView = this.FindControl<VideoView>("PlayerVideoView");
                if (videoView != null)
                {
                    videoView.MediaPlayer = vlcSession.MediaPlayer;
                }
            }

            vm.PlayerVm.PropertyChanged += OnPlayerVmPropertyChanged;
        }
    }

    private void OnPlayerVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsFullScreen) && sender is PlayerViewModel pvm)
        {
            if (pvm.IsFullScreen)
            {
                _previousState = WindowState;
                _previousPosition = Position;
                _previousSize = new Avalonia.Size(Bounds.Width, Bounds.Height);
                WindowState = WindowState.FullScreen;
            }
            else
            {
                WindowState = _previousState;
                if (_previousState == WindowState.Normal)
                {
                    Position = _previousPosition;
                    Width = _previousSize.Width;
                    Height = _previousSize.Height;
                }
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel vm && vm.PlayerVm.IsFullScreen)
        {
            vm.PlayerVm.IsFullScreen = false;
            e.Handled = true;
        }
    }

    private async System.Threading.Tasks.Task InitializeViewModelAsync(MainWindowViewModel vm)
    {
        try { await vm.InitializeAsync(); }
        catch (System.Exception) { vm.ReportInitializationFailure(); }
    }

    private async System.Threading.Tasks.Task<string?> PickLocalMediaAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "打开本地媒体（容器筛选不保证解码支持）", AllowMultiple = false,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("音视频媒体") { Patterns = LocalMediaService.MediaPatterns } }
        });
        if (files.Count == 0) return null;
        if (!files[0].Path.IsFile) throw new System.InvalidOperationException();
        return files[0].Path.AbsoluteUri;
    }

    private async void OnBrowseM3uClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "选择本地 M3U 播放列表", AllowMultiple = false,
                FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("M3U") { Patterns = new[] { "*.m3u", "*.m3u8" } } }
            });
            if (files.Count > 0 && files[0].Path.IsFile)
                vm.ConfigsVm.AddForm.LocationUrl = files[0].Path.LocalPath;
        }
        catch (System.Exception)
        {
            vm.ConfigsVm.AddForm.ValidationErrorMessage = "无法打开文件选择器，请手动输入文件路径";
        }
    }

    protected override void OnClosed(System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PickLocalMediaAsync = null;
            vm.CancelPendingLoad();
            vm.ChannelsVm.Dispose();
            vm.PlayerVm.Dispose();
            vm.ConfigsVm.ConfirmDeleteAsync = null;
            vm.PlayerVm.PropertyChanged -= OnPlayerVmPropertyChanged;
            var videoView = this.FindControl<VideoView>("PlayerVideoView");
            if (videoView != null) videoView.MediaPlayer = null;
            vm.PlayerVm.Session.Dispose();
        }
        base.OnClosed(e);
    }
}
