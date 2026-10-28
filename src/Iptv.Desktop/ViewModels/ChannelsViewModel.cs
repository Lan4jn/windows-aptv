using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Models;
using Iptv.Desktop.Services;

namespace Iptv.Desktop.ViewModels;

public sealed class ChannelsViewModel : ViewModelBase, IDisposable
{
    private readonly IPlaylistService _playlistService;
    private readonly ILibraryService _libraryService;
    private readonly IEpgService _epgService;
    private readonly IPlayerSession _player;
    private readonly Func<string?>? _globalEpgProvider;

    private readonly TimeProvider _clock;
    private readonly ITimer _programmeTimer;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _programmeCancellation;
    private int _programmeVersion;
    private bool _disposed;

    private SourceConfig? _currentConfig;
    private string _selectedGroup = "全部";
    private string _searchText = string.Empty;
    private Channel? _selectedChannel;
    private string? _currentProgrammeTitle;
    private string? _nextProgrammeTitle;
    private string? _currentProgrammeTime;
    private string? _nextProgrammeTime;
    private bool _isLoading;
    private string? _statusMessage;

    public List<Channel> AllChannels { get; } = new();
    public ObservableCollection<Channel> FilteredChannels { get; } = new();
    public ObservableCollection<string> Groups { get; } = new();

    public SourceConfig? CurrentConfig
    {
        get => _currentConfig;
        private set => SetProperty(ref _currentConfig, value);
    }

    public string SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                ApplyFilter();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public Channel? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (SetProperty(ref _selectedChannel, value))
            {
                _ = UpdateEpgAsync(value);
            }
        }
    }

    public string? CurrentProgrammeTitle
    {
        get => _currentProgrammeTitle;
        private set => SetProperty(ref _currentProgrammeTitle, value);
    }

    public string? NextProgrammeTitle
    {
        get => _nextProgrammeTitle;
        private set => SetProperty(ref _nextProgrammeTitle, value);
    }

    public string? CurrentProgrammeTime
    {
        get => _currentProgrammeTime;
        private set => SetProperty(ref _currentProgrammeTime, value);
    }

    public string? NextProgrammeTime
    {
        get => _nextProgrammeTime;
        private set => SetProperty(ref _nextProgrammeTime, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public AsyncRelayCommand PlayChannelCommand { get; }
    public AsyncRelayCommand ToggleFavoriteCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public ChannelsViewModel(
        IPlaylistService playlistService,
        ILibraryService libraryService,
        IEpgService epgService,
        IPlayerSession player,
        Func<string?>? globalEpgProvider = null,
        TimeProvider? timeProvider = null)
    {
        _playlistService = playlistService ?? throw new ArgumentNullException(nameof(playlistService));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _epgService = epgService ?? throw new ArgumentNullException(nameof(epgService));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _globalEpgProvider = globalEpgProvider;
        _clock = timeProvider ?? TimeProvider.System;
        _programmeTimer = _clock.CreateTimer(state =>
        {
            if (timeProvider == null)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!_disposed) _ = UpdateEpgAsync(SelectedChannel); });
            else if (!_disposed) _ = UpdateEpgAsync(SelectedChannel);
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        PlayChannelCommand = new AsyncRelayCommand(async param =>
        {
            if (param is Channel channel)
            {
                SelectedChannel = channel;
                await _libraryService.AddHistoryAsync(channel);
                await _player.PlayAsync(channel);
            }
        });

        ToggleFavoriteCommand = new AsyncRelayCommand(async param =>
        {
            if (param is Channel channel)
            {
                var isFav = await _libraryService.IsFavoriteAsync(channel.Id);
                if (isFav)
                {
                    await _libraryService.RemoveFavoriteAsync(channel.Id);
                }
                else
                {
                    await _libraryService.AddFavoriteAsync(channel);
                }
            }
        });

        RefreshCommand = new AsyncRelayCommand(async () =>
        {
            if (_currentConfig != null)
            {
                await LoadChannelsForConfigAsync(_currentConfig, forceRefresh: true);
            }
        });
    }

    public void ReportInitializationFailure() => StatusMessage = "本地配置恢复失败，请检查数据权限或备份";

    private int _loadVersion;

    public void ClearChannels()
    {
        Interlocked.Increment(ref _loadVersion);
        _loadCancellation?.Cancel();
        _loadCancellation = null;
        InvalidateProgramme();
        _ = _epgService.EnsureGuideLoadedAsync(null);
        CurrentConfig = null;
        SelectedChannel = null;
        AllChannels.Clear();
        FilteredChannels.Clear();
        Groups.Clear();
        IsLoading = false;
        StatusMessage = "请添加或选择配置";
    }

    public async Task LoadChannelsForConfigAsync(SourceConfig config, CancellationToken ct = default, bool forceRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var version = Interlocked.Increment(ref _loadVersion);
        _loadCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loadCancellation = cancellation;
        ct = cancellation.Token;
        SelectedChannel = null;
        InvalidateProgramme();
        AllChannels.Clear();
        FilteredChannels.Clear();
        Groups.Clear();
        CurrentConfig = config;
        IsLoading = true;
        StatusMessage = "正在加载频道列表...";

        try
        {
            await _epgService.EnsureGuideLoadedAsync(null, ct);
            var result = await _playlistService.LoadChannelsAsync(config, ct);
            ct.ThrowIfCancellationRequested();
            if (version != _loadVersion) return;
            var channels = result.Channels;

            AllChannels.Clear();
            AllChannels.AddRange(channels);
            Groups.Clear();
            Groups.Add("全部");
            var distinctGroups = channels
                .Select(c => c.Group)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g);

            foreach (var group in distinctGroups)
            {
                Groups.Add(group!);
            }

            _selectedGroup = "全部";
            OnPropertyChanged(nameof(SelectedGroup));
            ApplyFilter();
            StatusMessage = $"共加载 {AllChannels.Count} 个频道";

            var globalDefault = _globalEpgProvider?.Invoke();
            try
            {
                await _epgService.EnsureGuideForSourceAsync(config, result.EpgUrls, globalDefault, ct, forceRefresh);
                ct.ThrowIfCancellationRequested();
                if (version != _loadVersion) return;
                await UpdateEpgAsync(SelectedChannel);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                if (version == _loadVersion)
                {
                    InvalidateProgramme();
                    StatusMessage = $"共加载 {AllChannels.Count} 个频道，节目加载失败，请稍后刷新";
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (version == _loadVersion) StatusMessage = "加载已取消";
        }
        catch (Exception)
        {
            if (version == _loadVersion) StatusMessage = "加载失败，请检查源地址、账号或网络连接";
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
                _loadCancellation = null;
            }
        }
    }

    private void ApplyFilter()
    {
        var filtered = AllChannels.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(_selectedGroup) && _selectedGroup != "全部")
        {
            filtered = filtered.Where(c => string.Equals(c.Group, _selectedGroup, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            filtered = filtered.Where(c => c.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
        }

        FilteredChannels.Clear();
        foreach (var ch in filtered)
        {
            FilteredChannels.Add(ch);
        }
    }

    private void InvalidateProgramme()
    {
        Interlocked.Increment(ref _programmeVersion);
        _programmeCancellation?.Cancel();
        _programmeCancellation = null;
        CurrentProgrammeTitle = null;
        NextProgrammeTitle = null;
        CurrentProgrammeTime = null;
        NextProgrammeTime = null;
    }

    private async Task UpdateEpgAsync(Channel? channel)
    {
        if (_disposed) return;
        var version = Interlocked.Increment(ref _programmeVersion);
        var loadVersion = _loadVersion;
        _programmeCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _programmeCancellation = cancellation;
        CurrentProgrammeTitle = null;
        NextProgrammeTitle = null;
        CurrentProgrammeTime = null;
        NextProgrammeTime = null;
        try
        {
            if (channel == null) return;
            var (now, next) = await _epgService.GetNowNextAsync(channel, _clock.GetUtcNow(), cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || version != _programmeVersion || loadVersion != _loadVersion || !ReferenceEquals(channel, SelectedChannel)) return;
            CurrentProgrammeTitle = now?.Title;
            NextProgrammeTitle = next?.Title;
            CurrentProgrammeTime = FormatProgrammeTime(now);
            NextProgrammeTime = FormatProgrammeTime(next);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (version == _programmeVersion && loadVersion == _loadVersion)
            {
                CurrentProgrammeTitle = null;
                NextProgrammeTitle = null;
                CurrentProgrammeTime = null;
                NextProgrammeTime = null;
            }
        }
        finally
        {
            if (version == _programmeVersion) _programmeCancellation = null;
        }
    }

    private static string? FormatProgrammeTime(EpgProgramme? programme)
        => programme == null ? null : $"{programme.Start.ToLocalTime():HH:mm}–{programme.End.ToLocalTime():HH:mm}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _programmeTimer.Dispose();
        Interlocked.Increment(ref _loadVersion);
        _loadCancellation?.Cancel();
        _loadCancellation = null;
        InvalidateProgramme();
    }
}
