using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Epg;
using Iptv.Core.Library;
using Iptv.Core.Models;
using Iptv.Core.Playlists;
using Iptv.Core.Security;
using Iptv.Core.Xtream;

namespace Iptv.Desktop.Services;

public sealed class CorePlaylistService : IPlaylistService
{
    private readonly IXtreamService? _xtreamService;
    private readonly HttpClient _httpClient;
    private readonly CoreProxyPolicyService _proxyPolicy;

    public CorePlaylistService(HttpClient? httpClient = null, IXtreamService? xtreamService = null, CoreProxyPolicyService? proxyPolicyService = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _proxyPolicy = proxyPolicyService ?? new CoreProxyPolicyService();
        _xtreamService = xtreamService;
    }

    public async Task<PlaylistChannelsResult> LoadChannelsAsync(SourceConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            var policy = await _proxyPolicy.ResolveSourceAsync(config, ct).ConfigureAwait(false);
            config = policy.Source;
            if (config.Kind == SourceKind.Xtream)
            {
                if (_xtreamService != null && policy.Resolution.Status == Iptv.Core.Networking.ProxyResolutionStatus.System)
                {
                    var (providedChannels, providedUrl) = await _xtreamService.LoadLiveChannelsWithEpgAsync(config, ct).ConfigureAwait(false);
                    return new PlaylistChannelsResult(providedChannels, string.IsNullOrWhiteSpace(providedUrl) ? Array.Empty<string>() : new[] { providedUrl }, Array.Empty<string>());
                }
                using var xtreamLease = policy.OpenClient(_httpClient);
                var (channels, xmltvUrl) = await CoreXtreamService.LoadWithClientAsync(xtreamLease.Client, config, ct).ConfigureAwait(false);
                return new PlaylistChannelsResult(channels, new[] { xmltvUrl }, Array.Empty<string>());
            }
            using var lease = policy.OpenClient(_httpClient);
            var result = await new PlaylistLoader(lease.Client).LoadAsync(config, ct).ConfigureAwait(false);
            return new PlaylistChannelsResult(result.Channels, result.EpgUrls, result.Warnings);
        }
        catch (OperationCanceledException) { throw new OperationCanceledException("频道请求已取消。", ct); }
        catch (Exception) { throw new InvalidOperationException("频道加载失败，请检查来源与代理配置。"); }
    }
}

public sealed class CoreLibraryService : ILibraryService
{
    private readonly LibraryStateStore _store;
    private readonly ICredentialStorage _credentialStorage;
    private readonly Func<Task<IReadOnlyList<Channel>>>? _channelsProvider;
    private LibraryState? _cachedState;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public CoreLibraryService(
        string appDataDirectory,
        ILibraryStateProtector? protector = null,
        ICredentialStorage? credentialStorage = null,
        Func<Task<IReadOnlyList<Channel>>>? channelsProvider = null)
    {
        _store = new LibraryStateStore(appDataDirectory, protector);
        _credentialStorage = credentialStorage ?? new Security.SessionMemoryCredentialStorage();
        _channelsProvider = channelsProvider;
    }

    private async Task<LibraryState> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_cachedState == null)
        {
            var loaded = await _store.LoadAsync(ct).ConfigureAwait(false);

            // Restore credentials from secure storage for Xtream sources
            var restoredSources = new List<SourceConfig>();
            foreach (var src in loaded.Sources)
            {
                if (src.Kind == SourceKind.Xtream && string.IsNullOrEmpty(src.Password))
                {
                    var savedPwd = await _credentialStorage.GetPasswordAsync(src.Id, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(savedPwd))
                    {
                        restoredSources.Add(src with { Password = savedPwd });
                        continue;
                    }
                }
                restoredSources.Add(src);
            }

            _cachedState = loaded with { Sources = restoredSources };
        }
        return _cachedState;
    }

    public async Task<IReadOnlyList<SourceConfig>> GetConfigsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            return state.Sources.OrderBy(source => source.Order).ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ProxyPolicyContext> GetProxyPolicyContextAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            return new ProxyPolicyContext(state.Sources.ToArray(), state.UserProxies.ToArray());
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<UserProxy>> GetUserProxiesAsync(CancellationToken ct = default)
        => (await GetProxyPolicyContextAsync(ct).ConfigureAwait(false)).Proxies;

    public async Task SaveUserProxyAsync(UserProxy proxy, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var updated = ProxyLibraryMutations.Save(await EnsureLoadedAsync(ct).ConfigureAwait(false), proxy);
            await _store.SaveAsync(updated, ct).ConfigureAwait(false);
            _cachedState = updated;
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
            var updated = ProxyLibraryMutations.Delete(await EnsureLoadedAsync(ct).ConfigureAwait(false), proxyId, active);
            await _store.SaveAsync(updated, ct).ConfigureAwait(false);
            _cachedState = updated;
        }
        finally { _lock.Release(); }
    }

    public async Task SetSourceProxyAsync(string sourceId, string? proxyId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var updated = ProxyLibraryMutations.Assign(await EnsureLoadedAsync(ct).ConfigureAwait(false), sourceId, proxyId);
            await _store.SaveAsync(updated, ct).ConfigureAwait(false);
            _cachedState = updated;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveSourceOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            var ids = orderedIds.ToArray();
            if (ids.Length != state.Sources.Count || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
                ids.Any(id => !state.Sources.Any(source => source.Id == id)))
                throw new ArgumentException("Source order must contain every source exactly once");
            var sources = ids.Select((id, index) => state.Sources.Single(source => source.Id == id) with { Order = index }).ToArray();
            var updated = state with { Sources = sources };
            await _store.SaveAsync(updated, ct).ConfigureAwait(false);
            _cachedState = updated;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveConfigAsync(SourceConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            var current = state.Sources.FirstOrDefault(source => source.Id == config.Id);
            config = current is null
                ? config with { Order = state.Sources.Count == 0 ? 0 : state.Sources.Max(source => source.Order) + 1 }
                : config with { Order = current.Order, ProxyId = current.ProxyId };
            var oldPassword = await _credentialStorage.GetPasswordAsync(config.Id, ct).ConfigureAwait(false);
            var credentialChanged = oldPassword != config.Password;
            var sources = state.Sources.Where(source => source.Id != config.Id).Append(config).OrderBy(source => source.Order).ToArray();
            try
            {
                if (credentialChanged)
                {
                    if (config.Password is null) await _credentialStorage.DeletePasswordAsync(config.Id, ct).ConfigureAwait(false);
                    else await _credentialStorage.SavePasswordAsync(config.Id, config.Password, ct).ConfigureAwait(false);
                }
                var updated = state with { Sources = sources };
                await _store.SaveAsync(updated, ct).ConfigureAwait(false);
                _cachedState = updated;
            }
            catch
            {
                if (credentialChanged)
                {
                    try
                    {
                        if (oldPassword is null) await _credentialStorage.DeletePasswordAsync(config.Id, CancellationToken.None).ConfigureAwait(false);
                        else await _credentialStorage.SavePasswordAsync(config.Id, oldPassword, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { _cachedState = null; throw new InvalidOperationException("Credential recovery failed; reload configuration before retrying"); }
                }
                throw;
            }
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteConfigAsync(string configId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            await _credentialStorage.DeletePasswordAsync(configId, ct).ConfigureAwait(false);

            var sources = state.Sources.Where(s => s.Id != configId).ToList();
            var updatedState = state with { Sources = sources };

            await _store.SaveAsync(updatedState, ct).ConfigureAwait(false);
            _cachedState = updatedState;
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
            var state = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            return state.Favorites;
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
            var state = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            var updatedState = state.WithFavorite(channel);
            await _store.SaveAsync(updatedState, ct).ConfigureAwait(false);
            _cachedState = updatedState;
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
            var state = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            var updatedState = state.WithoutFavorite(channelId);
            await _store.SaveAsync(updatedState, ct).ConfigureAwait(false);
            _cachedState = updatedState;
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
            var state = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            return state.Favorites.Any(f => f.Id == channelId);
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
            var state = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            return state.RecentHistory.Select(r => r.Channel).ToList();
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
            var state = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            var updatedState = state.WithRecentPlayed(channel, DateTimeOffset.UtcNow, maxLimit: 100);
            await _store.SaveAsync(updatedState, ct).ConfigureAwait(false);
            _cachedState = updatedState;
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
            var state = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            var updatedState = state with { RecentHistory = Array.Empty<RecentlyPlayedItem>() };
            await _store.SaveAsync(updatedState, ct).ConfigureAwait(false);
            _cachedState = updatedState;
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

public sealed class CoreEpgService : IEpgService
{
    private readonly HttpClient _httpClient;
    private readonly CoreProxyPolicyService _proxyPolicy;
    private readonly CoreProxyPolicyService _standalonePolicy = new();
    private readonly Dictionary<string, (EpgGuide Guide, DateTimeOffset Loaded)> _guideCache = new(StringComparer.Ordinal);
    private EpgGuide? _cachedGuide;
    private int _guideVersion;
    private readonly object _guideGate = new();

    public CoreEpgService(HttpClient? httpClient = null, CoreProxyPolicyService? proxyPolicyService = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _proxyPolicy = proxyPolicyService ?? new CoreProxyPolicyService();
    }

    private int BeginLoad()
    {
        lock (_guideGate)
        {
            Volatile.Write(ref _cachedGuide, null);
            return ++_guideVersion;
        }
    }

    public async Task EnsureGuideLoadedAsync(string? epgUrl, CancellationToken ct = default, bool forceRefresh = false)
    {
        var version = BeginLoad();
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(epgUrl)) return;
        // Compatibility calls have no source context and therefore only use the existing System client.
        var source = new SourceConfig("standalone-epg", "standalone EPG", SourceKind.M3u, epgUrl);
        try
        {
            var policy = await _standalonePolicy.ResolveSourceAsync(source, ct).ConfigureAwait(false);
            await LoadGuideAsync(policy, epgUrl, version, forceRefresh, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw new OperationCanceledException("节目请求已取消。", ct); }
        catch (HttpRequestException) { throw new HttpRequestException("节目网络请求失败，请检查来源与代理配置。"); }
        catch (Exception) { throw new InvalidOperationException("节目加载失败，请检查来源与代理配置。"); }
    }

    public async Task EnsureGuideForSourceAsync(SourceConfig source, IReadOnlyList<string> derivedEpgUrls, string? globalDefault = null, CancellationToken ct = default, bool forceRefresh = false)
    {
        var version = BeginLoad();
        try
        {
            var policy = await _proxyPolicy.ResolveSourceAsync(source, ct).ConfigureAwait(false);
            // Derived URLs belong to the playlist just loaded; never combine them with an edited source location.
            if (policy.Source.Location != source.Location || policy.Source.Kind != source.Kind
                || policy.Source.Username != source.Username || policy.Source.Password != source.Password)
                throw new InvalidOperationException("来源已变更，请重新加载频道。");
            var url = EpgGuide.SelectEpgUrl(policy.Source.CustomEpgUrl, derivedEpgUrls, globalDefault);
            if (string.IsNullOrWhiteSpace(url)) return;
            await LoadGuideAsync(policy, url, version, forceRefresh, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw new OperationCanceledException("节目请求已取消。", ct); }
        catch (Exception) { throw new InvalidOperationException("节目加载失败，请检查来源与代理配置。"); }
    }

    private async Task LoadGuideAsync(SourceProxyPolicy policy, string url, int version, bool forceRefresh, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = _proxyPolicy.GuideCacheKey(policy, url);
        lock (_guideGate)
        {
            if (forceRefresh) _guideCache.Remove(key);
            else if (_guideCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.Loaded < TimeSpan.FromMinutes(15))
            {
                if (version == _guideVersion) Volatile.Write(ref _cachedGuide, cached.Guide);
                return;
            }
        }
        using var lease = policy.OpenClient(_httpClient);
        // Per-operation handler carries origin headers without mutating borrowed/system clients.
        using var headerClient = new HttpClient(new EpgOriginHeadersHandler(lease.Client, policy.Source.Headers));
        var guide = await new EpgLoader(headerClient).LoadGuideAsync(url, ct, forceRefresh: true).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        lock (_guideGate)
        {
            if (version != _guideVersion || ct.IsCancellationRequested) return;
            if (_guideCache.Count >= 16) _guideCache.Clear();
            _guideCache[key] = (guide, DateTimeOffset.UtcNow);
            Volatile.Write(ref _cachedGuide, guide);
        }
    }

    private sealed class EpgOriginHeadersHandler(HttpClient client, IReadOnlyDictionary<string, string>? headers) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri);
            if (headers != null)
                foreach (var header in headers)
                    if (!forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value))
                        throw new InvalidOperationException("节目来源请求头无效。");
            return await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<(EpgProgramme? Now, EpgProgramme? Next)> GetNowNextAsync(Channel channel, DateTimeOffset time, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var guide = Volatile.Read(ref _cachedGuide);
        if (guide == null) return Task.FromResult<(EpgProgramme?, EpgProgramme?)>((null, null));
        var channelId = guide.MatchChannelId(channel);
        if (channelId == null) return Task.FromResult<(EpgProgramme?, EpgProgramme?)>((null, null));
        var (now, next) = guide.GetNowNext(channelId, time);
        return Task.FromResult<(EpgProgramme?, EpgProgramme?)>((now, next));
    }
}

public sealed class CoreXtreamService : IXtreamService
{
    private readonly HttpClient _httpClient;
    private readonly CoreProxyPolicyService _proxyPolicy;

    public CoreXtreamService(HttpClient? httpClient = null, CoreProxyPolicyService? proxyPolicyService = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _proxyPolicy = proxyPolicyService ?? new CoreProxyPolicyService();
    }

    public async Task<(IReadOnlyList<Channel> Channels, string XmltvUrl)> LoadLiveChannelsWithEpgAsync(SourceConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Kind != SourceKind.Xtream)
        {
            throw new ArgumentException("Source is not Xtream.", nameof(config));
        }

        try
        {
            var policy = await _proxyPolicy.ResolveSourceAsync(config, ct).ConfigureAwait(false);
            using var lease = policy.OpenClient(_httpClient);
            return await LoadWithClientAsync(lease.Client, policy.Source, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw new OperationCanceledException("频道请求已取消。", ct); }
        catch (Exception) { throw new InvalidOperationException("频道加载失败，请检查来源与代理配置。"); }
    }

    internal static async Task<(IReadOnlyList<Channel> Channels, string XmltvUrl)> LoadWithClientAsync(HttpClient httpClient, SourceConfig config, CancellationToken ct)
    {
        var client = new XtreamClient(httpClient, config.Location, config.Username ?? "", config.Password ?? "", config.Headers);
        await client.AuthenticateAsync(ct).ConfigureAwait(false);
        var channels = await client.GetLiveChannelsAsync(config.Id, ct).ConfigureAwait(false);
        return (channels, client.GetXmltvUrl());
    }
}
