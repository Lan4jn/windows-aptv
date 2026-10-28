using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Iptv.Core.Models;
using Iptv.Core.Security;
using Iptv.Desktop.Security;
using Iptv.Desktop.Services;

namespace Iptv.Desktop.ViewModels;

public enum NavigationSection
{
    Channels,
    Favorites,
    Configs,
    Settings,
    Search
}

public sealed class MainWindowViewModel : ViewModelBase
{
    private NavigationSection _currentSection = NavigationSection.Channels;
    private string _title = "IPTV 桌面播放器 - 频道";

    public NavigationSection CurrentSection
    {
        get => _currentSection;
        set
        {
            if (SetProperty(ref _currentSection, value))
            {
                UpdateTitle();
                OnPropertyChanged(nameof(IsChannelsSection));
                OnPropertyChanged(nameof(IsFavoritesSection));
                OnPropertyChanged(nameof(IsConfigsSection));
                OnPropertyChanged(nameof(IsSettingsSection));
                OnPropertyChanged(nameof(IsSearchSection));
            }
        }
    }

    public bool IsChannelsSection => CurrentSection == NavigationSection.Channels;
    public bool IsFavoritesSection => CurrentSection == NavigationSection.Favorites;
    public bool IsConfigsSection => CurrentSection == NavigationSection.Configs;
    public bool IsSettingsSection => CurrentSection == NavigationSection.Settings;
    public bool IsSearchSection => CurrentSection == NavigationSection.Search;

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public ChannelsViewModel ChannelsVm { get; }
    public FavoritesViewModel FavoritesVm { get; }
    public ConfigsViewModel ConfigsVm { get; }
    public SettingsViewModel SettingsVm { get; }
    public SearchViewModel SearchVm { get; }
    public PlayerViewModel PlayerVm { get; }

    public RelayCommand NavigateToChannelsCommand { get; }
    public RelayCommand NavigateToFavoritesCommand { get; }
    public RelayCommand NavigateToConfigsCommand { get; }
    public RelayCommand NavigateToSettingsCommand { get; }
    public RelayCommand NavigateToSearchCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public AsyncRelayCommand OpenLocalMediaCommand { get; }
    public Func<Task<string?>>? PickLocalMediaAsync { get; set; }

    public RelayCommand AddFirstSourceCommand { get; }

    private bool _sourceListLoaded;
    public bool HasNoSources => _sourceListLoaded && ConfigsVm.Configs.Count == 0;

    private string? _errorMessage;

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);

    public MainWindowViewModel(
        IPlaylistService? playlistService = null,
        ILibraryService? libraryService = null,
        IEpgService? epgService = null,
        IPlayerSession? player = null,
        ILibraryStateProtector? protector = null)
    {
        protector ??= RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsDpapiStateProtector()
            : new SessionMemoryStateProtector();

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvDesktop");

        SettingsVm = new SettingsViewModel(protector);


        if (libraryService == null)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var credentialStorage = new WindowsCredentialStorage(appDataDir);
                libraryService = new CoreLibraryService(
                    appDataDir,
                    protector,
                    credentialStorage,
                    () => Task.FromResult<IReadOnlyList<Channel>>(ChannelsVm?.AllChannels.ToList() ?? (IReadOnlyList<Channel>)Array.Empty<Channel>()));
            }
            else
            {
                libraryService = new MemoryOnlyLibraryService(
                    () => Task.FromResult<IReadOnlyList<Channel>>(ChannelsVm?.AllChannels.ToList() ?? (IReadOnlyList<Channel>)Array.Empty<Channel>()));
            }
        }

        var proxyPolicy = new CoreProxyPolicyService(libraryService);
        playlistService ??= new CorePlaylistService(proxyPolicyService: proxyPolicy);
        epgService ??= new CoreEpgService(proxyPolicyService: proxyPolicy);
        player ??= new LibVlcPlayerSession(preflight: proxyPolicy.CheckNativePlaybackAsync);

        ChannelsVm = new ChannelsViewModel(
            playlistService,
            libraryService,
            epgService,
            player,
            () => string.IsNullOrWhiteSpace(SettingsVm.GlobalDefaultEpgUrl) ? null : SettingsVm.GlobalDefaultEpgUrl);
        FavoritesVm = new FavoritesViewModel(libraryService, player);
        ConfigsVm = new ConfigsViewModel(libraryService);
        ConfigsVm.SelectionChangedAsync = SelectSourceAsync;
        ConfigsVm.Configs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoSources));
        SearchVm = new SearchViewModel(libraryService, player);
        PlayerVm = new PlayerViewModel(player, action =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
            else Avalonia.Threading.Dispatcher.UIThread.Post(action);
        });

        NavigateToChannelsCommand = new RelayCommand(() => NavigateTo(NavigationSection.Channels));
        NavigateToFavoritesCommand = new RelayCommand(() => NavigateTo(NavigationSection.Favorites));
        NavigateToConfigsCommand = new RelayCommand(() => NavigateTo(NavigationSection.Configs));
        NavigateToSettingsCommand = new RelayCommand(() => NavigateTo(NavigationSection.Settings));
        NavigateToSearchCommand = new RelayCommand(() => NavigateTo(NavigationSection.Search));
        DismissErrorCommand = new RelayCommand(DismissError);
        OpenLocalMediaCommand = new AsyncRelayCommand(async () =>
        {
            if (PickLocalMediaAsync is null) return;
            try { await OpenLocalMediaAsync(await PickLocalMediaAsync()); }
            catch { ErrorMessage = "打开本地媒体失败，请重新选择本地音视频文件"; }
        });
        AddFirstSourceCommand = new RelayCommand(() =>
        {
            NavigateTo(NavigationSection.Configs);
            ConfigsVm.OpenAddM3uDialogCommand.Execute(null);
        });
        WireOwnedCommands(
            ChannelsVm.PlayChannelCommand,
            ChannelsVm.ToggleFavoriteCommand,
            ChannelsVm.RefreshCommand,
            ConfigsVm.SaveAddFormCommand,
            ConfigsVm.DeleteConfigCommand,
            FavoritesVm.PlayChannelCommand,
            FavoritesVm.RemoveFavoriteCommand,
            FavoritesVm.RefreshFavoritesCommand,
            PlayerVm.PlayCommand,
            PlayerVm.PauseCommand,
            PlayerVm.ResumeCommand,
            PlayerVm.TogglePlayPauseCommand,
            PlayerVm.StopCommand,
            PlayerVm.SetVolumeCommand,
            SearchVm.PlayChannelCommand
        );
    }

    private void WireOwnedCommands(params AsyncRelayCommand[] commands)
    {
        foreach (var cmd in commands)
        {
            cmd.CommandFailed += (sender, ex) => HandleCommandError(ex);
        }
    }

    public async Task OpenLocalMediaAsync(string? path)
    {
        if (path is null) return;
        try
        {
            var channel = LocalMediaService.CreateChannel(path);
            await PlayerVm.PlayCommand.ExecuteAsync(channel);
            ErrorMessage = PlayerVm.State == PlayerState.Error ? "本地媒体播放失败，请检查文件或解码支持" : null;
        }
        catch { ErrorMessage = "打开本地媒体失败，请选择存在的普通本地音视频文件"; }
    }

    public void DismissError()
    {
        ErrorMessage = null;
    }

    public void HandleCommandError(Exception? ex)
    {
        if (ex is null or OperationCanceledException) return;

        // Sanitized generic error message without leaking sensitive credentials, URLs, or raw exception strings
        ErrorMessage = ex switch
        {
            TimeoutException => "网络请求超时，请检查网络连接后重试",
            IOException => "本地文件或存储读写失败",
            UnauthorizedAccessException => "操作未授权或凭据无效",
            ArgumentException => "请求参数无效，请检查配置内容",
            _ => "操作执行失败，请重试"
        };
    }

    private CancellationTokenSource? _sourceLoadCancellation;

    private async Task SelectSourceAsync(SourceConfig? config)
    {
        _sourceLoadCancellation?.Cancel();
        _sourceLoadCancellation?.Dispose();
        _sourceLoadCancellation = new CancellationTokenSource();
        if (config is null)
        {
            ChannelsVm.ClearChannels();
            return;
        }
        await ChannelsVm.LoadChannelsForConfigAsync(config, _sourceLoadCancellation.Token);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await ConfigsVm.InitializeAsync(ct);
        _sourceListLoaded = true;
        OnPropertyChanged(nameof(HasNoSources));
        await FavoritesVm.RefreshFavoritesAsync(ct);
    }

    public void ReportInitializationFailure() => ChannelsVm.ReportInitializationFailure();

    public void CancelPendingLoad() => _sourceLoadCancellation?.Cancel();

    public void NavigateTo(NavigationSection section)
    {
        CurrentSection = section;
        if (section == NavigationSection.Favorites)
        {
            _ = FavoritesVm.RefreshFavoritesAsync();
        }
        else if (section == NavigationSection.Configs)
        {
            _ = ConfigsVm.RefreshConfigsAsync();
        }
    }

    private void UpdateTitle()
    {
        Title = CurrentSection switch
        {
            NavigationSection.Channels => "IPTV 桌面播放器 - 频道",
            NavigationSection.Favorites => "IPTV 桌面播放器 - 收藏",
            NavigationSection.Configs => "IPTV 桌面播放器 - 配置",
            NavigationSection.Settings => "IPTV 桌面播放器 - 设置",
            NavigationSection.Search => "IPTV 桌面播放器 - 搜索",
            _ => "IPTV 桌面播放器"
        };
    }
}
