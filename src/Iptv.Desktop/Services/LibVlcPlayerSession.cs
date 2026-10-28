using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Models;
using LibVLCSharp.Shared;

[assembly: InternalsVisibleTo("Iptv.Desktop.Tests")]

namespace Iptv.Desktop.Services;

/// <summary>
/// Native LibVLC implementation of IPlayerSession for Avalonia VideoView integration.
/// Fail-friendly on platforms without native VLC binaries.
/// </summary>
public sealed class LibVlcPlayerSession : IPlayerSession
{
    private readonly Func<Channel, bool> _startPlayback;
    private readonly Func<Channel, CancellationToken, Task>? _preflight;
    private readonly object _playGate = new();
    private long _playGeneration;
    private CancellationTokenSource? _pendingPlay;
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private volatile bool _isDisposed;
    private int _volume = 100;
    private bool _isMuted;

    public PlayerState State { get; private set; } = PlayerState.Idle;
    public Channel? CurrentChannel { get; private set; }
    public string? ErrorMessage { get; private set; }

    public int Volume
    {
        get => _volume;
        set => SetVolumeAsync(value).GetAwaiter().GetResult();
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Mute = _isMuted;
            }
        }
    }

    public TimeSpan Position => _mediaPlayer != null && _mediaPlayer.Time >= 0
        ? TimeSpan.FromMilliseconds(_mediaPlayer.Time)
        : TimeSpan.Zero;

    public TimeSpan Duration => _mediaPlayer != null && _mediaPlayer.Length >= 0
        ? TimeSpan.FromMilliseconds(_mediaPlayer.Length)
        : TimeSpan.Zero;

    /// <summary>
    /// Exposes native MediaPlayer for Avalonia VideoView binding.
    /// </summary>
    public MediaPlayer? MediaPlayer => _mediaPlayer;

    public event EventHandler<PlayerState>? StateChanged;
    public event EventHandler<string?>? ErrorOccurred;

    public LibVlcPlayerSession(Func<Channel, CancellationToken, Task>? preflight = null)
    {
        _preflight = preflight;
        _startPlayback = StartNativePlayback;
        InitializeLibVlc();
    }

    internal LibVlcPlayerSession(Func<Channel, bool> startPlayback, Func<Channel, CancellationToken, Task>? preflight = null)
    {
        _preflight = preflight;
        _startPlayback = startPlayback;
    }

    private void InitializeLibVlc()
    {
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            _libVlc = new LibVLC("--no-video-title-show", "--quiet");
            _mediaPlayer = new MediaPlayer(_libVlc);

            _mediaPlayer.Playing += OnPlaying;
            _mediaPlayer.Paused += OnPaused;
            _mediaPlayer.Stopped += OnStopped;
            _mediaPlayer.EncounteredError += OnEncounteredError;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"LibVLC 初始化受限: {ex.Message}";
            State = PlayerState.Error;
            ErrorOccurred?.Invoke(this, ErrorMessage);
        }
    }

    private void OnPlaying(object? sender, EventArgs args) => UpdateState(PlayerState.Playing);
    private void OnPaused(object? sender, EventArgs args) => UpdateState(PlayerState.Paused);
    private void OnStopped(object? sender, EventArgs args) => UpdateState(PlayerState.Stopped);

    private void OnEncounteredError(object? sender, EventArgs args)
    {
        if (_isDisposed) return;
        ErrorMessage = "播放器遇到解码或网络流错误";
        UpdateState(PlayerState.Error);
        ErrorOccurred?.Invoke(this, ErrorMessage);
    }

    private void UpdateState(PlayerState newState)
    {
        if (_isDisposed) return;
        State = newState;
        StateChanged?.Invoke(this, newState);
    }

    public async Task PlayAsync(Channel channel, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(channel);
        ct.ThrowIfCancellationRequested();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationTokenSource? previous;
        long generation;
        lock (_playGate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            generation = ++_playGeneration;
            previous = _pendingPlay;
            _pendingPlay = operation;
            CurrentChannel = channel;
            ErrorMessage = null;
            UpdateState(PlayerState.Loading);
        }
        CancelPending(previous);
        var preflightComplete = false;
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            if (_preflight != null) await _preflight(channel, operation.Token).ConfigureAwait(false);
            preflightComplete = true;
            lock (_playGate)
            {
                if (_isDisposed || generation != _playGeneration) return;
                operation.Token.ThrowIfCancellationRequested();
                if (!_startPlayback(channel))
                    throw new InvalidOperationException("Native playback declined.");
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            lock (_playGate)
            {
                if (!_isDisposed && generation == _playGeneration)
                {
                    CurrentChannel = null;
                    UpdateState(PlayerState.Stopped);
                }
            }
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception)
        {
            lock (_playGate)
            {
                if (_isDisposed || generation != _playGeneration) return;
                if (operation.IsCancellationRequested)
                {
                    CurrentChannel = null;
                    UpdateState(PlayerState.Stopped);
                }
                else
                {
                    ErrorMessage = preflightComplete ? "起播失败，请检查媒体核心或网络流" : "代理配置不受当前播放路径支持或策略检查失败";
                    UpdateState(PlayerState.Error);
                    ErrorOccurred?.Invoke(this, ErrorMessage);
                }
            }
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_playGate)
            {
                if (ReferenceEquals(_pendingPlay, operation)) _pendingPlay = null;
            }
        }
    }

    private static void CancelPending(CancellationTokenSource? pending)
    {
        try { pending?.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
    }

    private bool StartNativePlayback(Channel channel)
    {
        if (_libVlc == null || _mediaPlayer == null)
            throw new InvalidOperationException("未检测到可用的 LibVLC 原生媒体核心，无法播放视频");
        _currentMedia?.Dispose();

        if (Uri.TryCreate(channel.StreamUrl, UriKind.Absolute, out var uri))
        {
            _currentMedia = new Media(_libVlc!, uri);
        }
        else
        {
            _currentMedia = new Media(_libVlc!, channel.StreamUrl, FromType.FromPath);
        }

        // Apply custom HTTP headers if present
        if (channel.Headers != null)
        {
            foreach (var (key, value) in channel.Headers)
            {
                if (key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                {
                    _currentMedia.AddOption($":http-user-agent={value}");
                }
                else if (key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                {
                    _currentMedia.AddOption($":http-referrer={value}");
                }
            }
        }

        return _mediaPlayer!.Play(_currentMedia);
    }

    public Task PauseAsync()
    {
        if (_mediaPlayer != null && State == PlayerState.Playing)
        {
            _mediaPlayer.SetPause(true);
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        if (_mediaPlayer != null && State == PlayerState.Paused)
        {
            _mediaPlayer.SetPause(false);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        CancellationTokenSource? pending;
        lock (_playGate)
        {
            ++_playGeneration;
            pending = _pendingPlay;
            _pendingPlay = null;
            CurrentChannel = null;
            _mediaPlayer?.Stop();
            UpdateState(PlayerState.Stopped);
        }
        CancelPending(pending);
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Volume = _volume;
        }
        return Task.CompletedTask;
    }

    public void ToggleMute()
    {
        IsMuted = !IsMuted;
    }

    public void Dispose()
    {
        CancellationTokenSource? pending;
        lock (_playGate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            ++_playGeneration;
            pending = _pendingPlay;
            _pendingPlay = null;
        }
        CancelPending(pending);

        if (_mediaPlayer != null)
        {
            _mediaPlayer.Playing -= OnPlaying;
            _mediaPlayer.Paused -= OnPaused;
            _mediaPlayer.Stopped -= OnStopped;
            _mediaPlayer.EncounteredError -= OnEncounteredError;
        }

        try { _mediaPlayer?.Stop(); }
        catch { }
        DisposeSafely(_mediaPlayer);
        DisposeSafely(_currentMedia);
        DisposeSafely(_libVlc);
        _mediaPlayer = null;
        _currentMedia = null;
        _libVlc = null;
    }

    private static void DisposeSafely(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch { }
    }
}
