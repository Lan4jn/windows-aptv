using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Models;
using Iptv.Core.Library;

namespace Iptv.Desktop.Services;

/// <summary>
/// In-memory implementation of ILibraryService for macOS development/testing.
/// Keeps all sources, credentials, and favorites strictly in session memory
/// without writing sensitive unencrypted state to disk.
/// </summary>
public sealed class MemoryOnlyLibraryService : ILibraryService
{
    private readonly List<SourceConfig> _configs = new();
    private readonly List<UserProxy> _proxies = new();
    private readonly List<Channel> _favorites = new();
    private readonly List<Channel> _history = new();
    private readonly Func<Task<IReadOnlyList<Channel>>>? _channelsProvider;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MemoryOnlyLibraryService(Func<Task<IReadOnlyList<Channel>>>? channelsProvider = null)
    {
        _channelsProvider = channelsProvider;
    }

    public async Task<IReadOnlyList<SourceConfig>> GetConfigsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _configs.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ProxyPolicyContext> GetProxyPolicyContextAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return new ProxyPolicyContext(_configs.ToArray(), _proxies.ToArray()); }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<UserProxy>> GetUserProxiesAsync(CancellationToken ct = default)
        => (await GetProxyPolicyContextAsync(ct).ConfigureAwait(false)).Proxies;

    private LibraryState ProxyState() => new()
    {
        Sources = _configs.ToArray(), UserProxies = _proxies.ToArray(), Favorites = _favorites.ToArray(),
        RecentHistory = _history.Select(c => new RecentlyPlayedItem(c, DateTimeOffset.UnixEpoch)).ToArray()
    };

    public async Task SaveUserProxyAsync(UserProxy proxy, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var updated = ProxyLibraryMutations.Save(ProxyState(), proxy);
            _proxies.Clear(); _proxies.AddRange(updated.UserProxies);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteUserProxyAsync(string proxyId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var active = _channelsProvider == null ? null : await _channelsProvider().WaitAsync(ct).ConfigureAwait(false);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var updated = ProxyLibraryMutations.Delete(ProxyState(), proxyId, active);
            _proxies.Clear(); _proxies.AddRange(updated.UserProxies);
        }
        finally { _lock.Release(); }
    }

    public async Task SetSourceProxyAsync(string sourceId, string? proxyId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var updated = ProxyLibraryMutations.Assign(ProxyState(), sourceId, proxyId);
            _configs.Clear(); _configs.AddRange(updated.Sources);
        }
        finally { _lock.Release(); }
    }

    public async Task SaveSourceOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ids = orderedIds.ToArray();
            if (ids.Length != _configs.Count || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => !_configs.Any(source => source.Id == id)))
                throw new ArgumentException("Source order must contain every source exactly once");
            var updated = ids.Select((id, index) => _configs.Single(source => source.Id == id) with { Order = index }).ToArray();
            _configs.Clear();
            _configs.AddRange(updated);
        }
        finally { _lock.Release(); }
    }

    public async Task SaveConfigAsync(SourceConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _configs.FirstOrDefault(source => source.Id == config.Id);
            config = current is null ? config with { Order = _configs.Count == 0 ? 0 : _configs.Max(source => source.Order) + 1 }
                : config with { Order = current.Order, ProxyId = current.ProxyId };
            _configs.RemoveAll(source => source.Id == config.Id);
            _configs.Add(config);
            _configs.Sort((left, right) => left.Order.CompareTo(right.Order));
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteConfigAsync(string configId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _configs.RemoveAll(c => c.Id == configId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<Channel>> GetFavoritesAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _favorites.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddFavoriteAsync(Channel channel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_favorites.Any(f => f.Id == channel.Id))
            {
                _favorites.Add(channel);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveFavoriteAsync(string channelId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _favorites.RemoveAll(f => f.Id == channelId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> IsFavoriteAsync(string channelId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _favorites.Any(f => f.Id == channelId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<Channel>> GetHistoryAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _history.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddHistoryAsync(Channel channel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _history.RemoveAll(h => h.Id == channel.Id);
            _history.Insert(0, channel);
            if (_history.Count > 100)
            {
                _history.RemoveAt(_history.Count - 1);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearHistoryAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _history.Clear();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<Channel>> GetAllChannelsAsync(CancellationToken ct = default)
    {
        if (_channelsProvider != null)
        {
            return await _channelsProvider().ConfigureAwait(false);
        }
        return Array.Empty<Channel>();
    }
}
