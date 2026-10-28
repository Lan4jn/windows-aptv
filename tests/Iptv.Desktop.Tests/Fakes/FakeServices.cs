using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Models;
using Iptv.Desktop.Services;

namespace Iptv.Desktop.Tests;

public class FakePlaylistService : IPlaylistService
{
    private readonly IReadOnlyList<Channel> _channels;
    private readonly IReadOnlyList<string> _epgUrls;

    public FakePlaylistService(IReadOnlyList<Channel>? channels = null, IReadOnlyList<string>? epgUrls = null)
    {
        _channels = channels ?? Array.Empty<Channel>();
        _epgUrls = epgUrls ?? Array.Empty<string>();
    }

    public Func<SourceConfig, CancellationToken, Task<PlaylistChannelsResult>>? LoadHandler { get; set; }

    public Task<PlaylistChannelsResult> LoadChannelsAsync(SourceConfig config, CancellationToken ct = default)
    {
        return LoadHandler?.Invoke(config, ct) ?? Task.FromResult(new PlaylistChannelsResult(_channels, _epgUrls, Array.Empty<string>()));
    }
}

public class FakeLibraryService : ILibraryService
{
    private readonly List<SourceConfig> _configs = new();
    private readonly List<UserProxy> _proxies = new();
    private readonly List<Channel> _favorites = new();
    private readonly List<Channel> _history = new();
    private readonly List<Channel> _allChannels = new();

    public IReadOnlyList<SourceConfig> Configs => _configs;
    public IReadOnlyList<Channel> Favorites => _favorites;

    public FakeLibraryService(
        IEnumerable<SourceConfig>? configs = null,
        IEnumerable<Channel>? allChannels = null)
    {
        if (configs != null) _configs.AddRange(configs);
        if (allChannels != null) _allChannels.AddRange(allChannels);
    }

    public Task<IReadOnlyList<SourceConfig>> GetConfigsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SourceConfig>>(_configs.OrderBy(source => source.Order).ToList());

    public Task<ProxyPolicyContext> GetProxyPolicyContextAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ProxyPolicyContext(_configs.ToArray(), _proxies.ToArray()));
    }

    public Task<IReadOnlyList<UserProxy>> GetUserProxiesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<UserProxy>>(_proxies.ToArray());
    }

    public Task SaveUserProxyAsync(UserProxy proxy, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _proxies.RemoveAll(p => p.Id == proxy.Id); _proxies.Add(proxy);
        return Task.CompletedTask;
    }

    public Task DeleteUserProxyAsync(string proxyId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_configs.Any(s => s.ProxyId == proxyId) || _favorites.Concat(_history).Concat(_allChannels).Any(c => c.ProxyId == proxyId))
            throw new InvalidOperationException("代理档案仍被引用，请先解除关联。");
        _proxies.RemoveAll(p => p.Id == proxyId);
        return Task.CompletedTask;
    }

    public Task SetSourceProxyAsync(string sourceId, string? proxyId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (proxyId != null && !_proxies.Any(p => p.Id == proxyId)) throw new InvalidOperationException("代理档案不存在。");
        var index = _configs.FindIndex(s => s.Id == sourceId);
        if (index < 0) throw new InvalidOperationException("来源配置不存在。");
        _configs[index] = _configs[index] with { ProxyId = proxyId };
        return Task.CompletedTask;
    }

    public Task SaveSourceOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ids = orderedIds.ToArray();
        if (ids.Length != _configs.Count || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => !_configs.Any(source => source.Id == id)))
            throw new ArgumentException("Source order must contain every source exactly once");
        var updated = ids.Select((id, index) => _configs.Single(source => source.Id == id) with { Order = index }).ToArray();
        _configs.Clear();
        _configs.AddRange(updated);
        return Task.CompletedTask;
    }

    public Task SaveConfigAsync(SourceConfig config, CancellationToken ct = default)
    {
        var current = _configs.FirstOrDefault(source => source.Id == config.Id);
        config = current is null ? config with { Order = _configs.Count == 0 ? 0 : _configs.Max(source => source.Order) + 1 }
            : config with { Order = current.Order, ProxyId = current.ProxyId };
        _configs.RemoveAll(c => c.Id == config.Id);
        _configs.Add(config);
        return Task.CompletedTask;
    }

    public Task DeleteConfigAsync(string configId, CancellationToken ct = default)
    {
        _configs.RemoveAll(c => c.Id == configId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Channel>> GetFavoritesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Channel>>(_favorites.ToList());

    public Task AddFavoriteAsync(Channel channel, CancellationToken ct = default)
    {
        if (!_favorites.Any(f => f.Id == channel.Id))
            _favorites.Add(channel);
        return Task.CompletedTask;
    }

    public Task RemoveFavoriteAsync(string channelId, CancellationToken ct = default)
    {
        _favorites.RemoveAll(f => f.Id == channelId);
        return Task.CompletedTask;
    }

    public Task<bool> IsFavoriteAsync(string channelId, CancellationToken ct = default)
        => Task.FromResult(_favorites.Any(f => f.Id == channelId));

    public Task<IReadOnlyList<Channel>> GetHistoryAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Channel>>(_history.ToList());

    public Task AddHistoryAsync(Channel channel, CancellationToken ct = default)
    {
        _history.RemoveAll(h => h.Id == channel.Id);
        _history.Insert(0, channel);
        return Task.CompletedTask;
    }

    public Task ClearHistoryAsync(CancellationToken ct = default)
    {
        _history.Clear();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Channel>> GetAllChannelsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Channel>>(_allChannels.ToList());
}

public class FakeEpgService : IEpgService
{
    private readonly EpgProgramme? _now;
    private readonly EpgProgramme? _next;

    public FakeEpgService(EpgProgramme? now = null, EpgProgramme? next = null)
    {
        _now = now;
        _next = next;
    }

    public Func<string?, CancellationToken, Task>? LoadHandler { get; set; }
    public List<string?> LoadedUrls { get; } = new();
    public List<bool> RefreshFlags { get; } = new();
    public Func<Channel, DateTimeOffset, CancellationToken, Task<(EpgProgramme?, EpgProgramme?)>>? NowNextHandler { get; set; }

    public Task EnsureGuideLoadedAsync(string? epgUrl, CancellationToken ct = default, bool forceRefresh = false)
    {
        LoadedUrls.Add(epgUrl);
        RefreshFlags.Add(forceRefresh);
        return LoadHandler?.Invoke(epgUrl, ct) ?? Task.CompletedTask;
    }

    public List<string> SourceContexts { get; } = new();
    public Task EnsureGuideForSourceAsync(SourceConfig source, IReadOnlyList<string> derivedEpgUrls, string? globalDefault = null, CancellationToken ct = default, bool forceRefresh = false)
    {
        SourceContexts.Add(source.Id);
        return EnsureGuideLoadedAsync(Iptv.Core.Epg.EpgGuide.SelectEpgUrl(source.CustomEpgUrl, derivedEpgUrls, globalDefault), ct, forceRefresh);
    }

    public Task<(EpgProgramme? Now, EpgProgramme? Next)> GetNowNextAsync(Channel channel, DateTimeOffset time, CancellationToken ct = default)
    {
        return NowNextHandler?.Invoke(channel, time, ct) ?? Task.FromResult((_now, _next));
    }
}

public class FakePlayer : IPlayerSession
{
    public PlayerState State { get; private set; } = PlayerState.Idle;
    public Channel? CurrentChannel { get; private set; }
    public int Volume { get; set; } = 100;
    public bool IsMuted { get; set; }
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }
    public string? ErrorMessage { get; private set; }

    public event EventHandler<PlayerState>? StateChanged;
    public event EventHandler<string?>? ErrorOccurred;

    public void TriggerError(string error)
    {
        ErrorMessage = error;
        ErrorOccurred?.Invoke(this, error);
    }

    public Task PlayAsync(Channel channel, CancellationToken ct = default)
    {
        CurrentChannel = channel;
        State = PlayerState.Playing;
        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        if (State == PlayerState.Playing)
        {
            State = PlayerState.Paused;
            StateChanged?.Invoke(this, State);
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        if (State == PlayerState.Paused)
        {
            State = PlayerState.Playing;
            StateChanged?.Invoke(this, State);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        State = PlayerState.Stopped;
        CurrentChannel = null;
        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(int volume)
    {
        Volume = Math.Clamp(volume, 0, 100);
        return Task.CompletedTask;
    }

    public void ToggleMute()
    {
        IsMuted = !IsMuted;
    }

    public void Dispose()
    {
    }
}
