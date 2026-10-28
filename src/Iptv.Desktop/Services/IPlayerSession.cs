using System;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Models;

namespace Iptv.Desktop.Services;

public enum PlayerState
{
    Idle,
    Loading,
    Playing,
    Paused,
    Stopped,
    Error
}

/// <summary>
/// Abstraction for a media playback session to support real LibVLC VideoView
/// and offline headless unit tests without requiring native binaries.
/// </summary>
public interface IPlayerSession : IDisposable
{
    PlayerState State { get; }
    Channel? CurrentChannel { get; }
    int Volume { get; set; } // 0 - 100
    bool IsMuted { get; set; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    string? ErrorMessage { get; }

    event EventHandler<PlayerState>? StateChanged;
    event EventHandler<string?>? ErrorOccurred;

    Task PlayAsync(Channel channel, CancellationToken ct = default);
    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();
    Task SetVolumeAsync(int volume);
    void ToggleMute();
}
