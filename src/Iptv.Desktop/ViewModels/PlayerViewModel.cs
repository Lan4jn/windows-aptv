using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Iptv.Core.Models;
using Iptv.Desktop.Services;

namespace Iptv.Desktop.ViewModels;

public sealed class PlayerViewModel : ViewModelBase, IDisposable
{
    private readonly IPlayerSession _session;
    private bool _isFullScreen;
    private readonly Action<Action> _dispatchNotification;
    private volatile bool _isDisposed;

    public IPlayerSession Session => _session;

    public PlayerState State => _session.State;
    public Channel? CurrentChannel => _session.CurrentChannel;
    public string? ChannelTitle => CurrentChannel?.Name;
    public string? StreamUrl => CurrentChannel?.StreamUrl;
    public string? SafeDisplayStreamUrl => SourceConfigItemViewModel.SanitizeUrl(CurrentChannel?.StreamUrl);
    public string? ErrorMessage => _session.ErrorMessage;

    public bool IsPlaying => _session.State == PlayerState.Playing;
    public bool IsPaused => _session.State == PlayerState.Paused;
    public bool IsLoading => _session.State == PlayerState.Loading;

    public int Volume
    {
        get => _session.Volume;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (_session.Volume != clamped)
            {
                _session.SetVolumeAsync(clamped);
                OnPropertyChanged();
            }
        }
    }

    public bool IsMuted
    {
        get => _session.IsMuted;
        set
        {
            if (_session.IsMuted != value)
            {
                _session.IsMuted = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsFullScreen
    {
        get => _isFullScreen;
        set => SetProperty(ref _isFullScreen, value);
    }

    public AsyncRelayCommand PlayCommand { get; }
    public AsyncRelayCommand PauseCommand { get; }
    public AsyncRelayCommand ResumeCommand { get; }
    public AsyncRelayCommand TogglePlayPauseCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand SetVolumeCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }
    public RelayCommand ToggleFullScreenCommand { get; }

    public PlayerViewModel(IPlayerSession session, Action<Action>? dispatchNotification = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatchNotification = dispatchNotification ?? (action => action());
        _session.StateChanged += OnStateChanged;
        _session.ErrorOccurred += OnErrorOccurred;

        PlayCommand = new AsyncRelayCommand(async param =>
        {
            if (param is Channel channel)
            {
                await _session.PlayAsync(channel);
            }
        });

        PauseCommand = new AsyncRelayCommand(() => _session.PauseAsync());
        ResumeCommand = new AsyncRelayCommand(() => _session.ResumeAsync());
        TogglePlayPauseCommand = new AsyncRelayCommand(async () =>
        {
            if (IsPlaying) await _session.PauseAsync();
            else if (IsPaused) await _session.ResumeAsync();
        });

        StopCommand = new AsyncRelayCommand(() => _session.StopAsync());

        SetVolumeCommand = new AsyncRelayCommand(param =>
        {
            if (param is int vol)
            {
                Volume = vol;
            }
            return Task.CompletedTask;
        });

        ToggleMuteCommand = new RelayCommand(() =>
        {
            _session.ToggleMute();
            OnPropertyChanged(nameof(IsMuted));
        });

        ToggleFullScreenCommand = new RelayCommand(() =>
        {
            IsFullScreen = !IsFullScreen;
        });
    }

    private void OnStateChanged(object? sender, PlayerState state)
    {
        DispatchNotification(() => NotifyStateProperties());
    }

    private void NotifyStateProperties()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(CurrentChannel));
        OnPropertyChanged(nameof(ChannelTitle));
        OnPropertyChanged(nameof(StreamUrl));
        OnPropertyChanged(nameof(SafeDisplayStreamUrl));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ErrorMessage));
    }

    private void OnErrorOccurred(object? sender, string? error)
    {
        DispatchNotification(() => OnPropertyChanged(nameof(ErrorMessage)));
    }

    private void DispatchNotification(Action notification)
    {
        if (_isDisposed) return;
        _dispatchNotification(() =>
        {
            if (!_isDisposed) notification();
        });
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _session.StateChanged -= OnStateChanged;
        _session.ErrorOccurred -= OnErrorOccurred;
    }
}
