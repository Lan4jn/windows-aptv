using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Models;

namespace Iptv.Desktop.Services;

public sealed record PlaylistChannelsResult(
    IReadOnlyList<Channel> Channels,
    IReadOnlyList<string> EpgUrls,
    IReadOnlyList<string> Warnings);

public interface IPlaylistService
{
    Task<PlaylistChannelsResult> LoadChannelsAsync(SourceConfig config, CancellationToken ct = default);
}

public sealed record ProxyPolicyContext(IReadOnlyList<SourceConfig> Sources, IReadOnlyList<UserProxy> Proxies);

public interface ILibraryService
{
    Task<ProxyPolicyContext> GetProxyPolicyContextAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UserProxy>> GetUserProxiesAsync(CancellationToken ct = default);
    Task SaveUserProxyAsync(UserProxy proxy, CancellationToken ct = default);
    Task DeleteUserProxyAsync(string proxyId, CancellationToken ct = default);
    Task SetSourceProxyAsync(string sourceId, string? proxyId, CancellationToken ct = default);
    Task<IReadOnlyList<SourceConfig>> GetConfigsAsync(CancellationToken ct = default);
    Task SaveConfigAsync(SourceConfig config, CancellationToken ct = default);
    Task SaveSourceOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default);
    Task DeleteConfigAsync(string configId, CancellationToken ct = default);
    Task<IReadOnlyList<Channel>> GetFavoritesAsync(CancellationToken ct = default);
    Task AddFavoriteAsync(Channel channel, CancellationToken ct = default);
    Task RemoveFavoriteAsync(string channelId, CancellationToken ct = default);
    Task<bool> IsFavoriteAsync(string channelId, CancellationToken ct = default);
    Task<IReadOnlyList<Channel>> GetHistoryAsync(CancellationToken ct = default);
    Task AddHistoryAsync(Channel channel, CancellationToken ct = default);
    Task ClearHistoryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Channel>> GetAllChannelsAsync(CancellationToken ct = default);
}

public interface IEpgService
{
    Task EnsureGuideLoadedAsync(string? epgUrl, CancellationToken ct = default, bool forceRefresh = false);
    Task EnsureGuideForSourceAsync(SourceConfig source, IReadOnlyList<string> derivedEpgUrls, string? globalDefault = null, CancellationToken ct = default, bool forceRefresh = false);
    Task<(EpgProgramme? Now, EpgProgramme? Next)> GetNowNextAsync(Channel channel, DateTimeOffset time, CancellationToken ct = default);
}

public interface IXtreamService
{
    Task<(IReadOnlyList<Channel> Channels, string XmltvUrl)> LoadLiveChannelsWithEpgAsync(SourceConfig config, CancellationToken ct = default);
}
