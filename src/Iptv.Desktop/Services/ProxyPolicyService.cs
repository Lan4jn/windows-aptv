using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Library;
using Iptv.Core.Models;
using Iptv.Core.Networking;

namespace Iptv.Desktop.Services;

internal static class ProxyLibraryMutations
{
    public static LibraryState Save(LibraryState state, UserProxy proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        _ = new UserProxy(proxy.Id, proxy.Name, proxy.Kind, proxy.Endpoint, proxy.Username, proxy.Password);
        return state with { UserProxies = state.UserProxies.Where(p => p.Id != proxy.Id).Append(proxy).ToArray() };
    }

    public static LibraryState Delete(LibraryState state, string proxyId, IEnumerable<Channel>? activeChannels = null)
    {
        if (state.Sources.Any(s => s.ProxyId == proxyId) || state.Favorites.Any(c => c.ProxyId == proxyId)
            || state.RecentHistory.Any(r => r.Channel.ProxyId == proxyId) || activeChannels?.Any(c => c.ProxyId == proxyId) == true)
            throw new InvalidOperationException("代理档案仍被引用，请先解除关联。");
        return state with { UserProxies = state.UserProxies.Where(p => p.Id != proxyId).ToArray() };
    }

    public static LibraryState Assign(LibraryState state, string sourceId, string? proxyId)
    {
        if (state.Sources.Count(s => s.Id == sourceId) != 1) throw new InvalidOperationException("来源配置不存在或不唯一。");
        if (proxyId != null && !state.UserProxies.Any(p => p.Id == proxyId)) throw new InvalidOperationException("代理档案不存在。");
        return state with { Sources = state.Sources.Select(s => s.Id == sourceId ? s with { ProxyId = proxyId } : s).ToArray() };
    }
}

public sealed class SourceProxyPolicy
{
    public SourceConfig Source { get; }
    public ProxyResolution Resolution { get; }
    public string CacheIdentity { get; }
    internal SourceProxyPolicy(SourceConfig source, ProxyResolution resolution, string cacheIdentity)
    {
        Source = source;
        Resolution = resolution;
        CacheIdentity = cacheIdentity;
    }
    public ProxyHttpClientLease OpenClient(HttpClient systemClient) => Resolution.Status == ProxyResolutionStatus.System
        ? ProxyHttpClientFactory.BorrowSystem(systemClient) : ProxyHttpClientFactory.Create(Resolution);
    public override string ToString() => $"SourceProxyPolicy {{ Status = {Resolution.Status} }}";
}

/// <summary>Resolves a fresh coherent library snapshot for each operation; never changes an in-flight handler.</summary>
public sealed class CoreProxyPolicyService
{
    private readonly ILibraryService? _library;
    private readonly byte[] _identityKey = RandomNumberGenerator.GetBytes(32);
    public CoreProxyPolicyService(ILibraryService? library = null) => _library = library;

    public async Task<SourceProxyPolicy> ResolveSourceAsync(SourceConfig source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();
        ValidateOriginHeaders(source);
        IReadOnlyList<UserProxy> profiles = Array.Empty<UserProxy>();
        if (_library != null)
        {
            var context = await _library.GetProxyPolicyContextAsync(ct).ConfigureAwait(false);
            var matches = context.Sources.Where(s => s.Id == source.Id).Take(2).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("来源代理策略不可用，请刷新配置。");
            source = matches[0];
            profiles = context.Proxies;
        }
        ValidateOriginHeaders(source);
        var resolution = ProxyPolicyResolver.ResolveSource(source, profiles);
        if (resolution.Status == ProxyResolutionStatus.Error)
            throw new InvalidOperationException("代理策略无效，请检查档案和来源关联。");
        var proxy = source.ProxyId == null ? null : profiles.Single(p => p.Id == source.ProxyId);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(source.Id);
            writer.Write(source.Location);
            writer.Write((int)source.Kind);
            writer.Write(source.Username ?? "");
            writer.Write(source.Password ?? "");
            writer.Write(source.ProxyId ?? "");
            writer.Write(proxy?.Endpoint ?? "");
            writer.Write((int)(proxy?.Kind ?? ProxyKind.Http));
            writer.Write(proxy?.Username ?? "");
            writer.Write(proxy?.Password ?? "");
            if (source.Headers != null)
                foreach (var header in source.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
                { writer.Write(header.Key); writer.Write(header.Value); }
        }
        return new SourceProxyPolicy(source, resolution, Convert.ToHexString(HMACSHA256.HashData(_identityKey, bytes.ToArray())));
    }

    internal string GuideCacheKey(SourceProxyPolicy policy, string url)
        => Convert.ToHexString(HMACSHA256.HashData(_identityKey, Encoding.UTF8.GetBytes(policy.CacheIdentity + "\n" + url)));

    public async Task CheckNativePlaybackAsync(Channel channel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ct.ThrowIfCancellationRequested();
        if (LocalMediaService.IsFileCandidate(channel.StreamUrl))
        {
            _ = LocalMediaService.ValidateFile(channel.StreamUrl);
            if (channel.SourceId != LocalMediaService.SourceMarker || channel.ProxyId is not null || channel.Headers?.Count > 0)
                throw new InvalidOperationException("本地播放路径或代理策略无效。");
            if (_library is not null)
            {
                var localContext = await _library.GetProxyPolicyContextAsync(ct).ConfigureAwait(false);
                if (localContext.Sources.Any(source => source.Id == LocalMediaService.SourceMarker))
                    throw new InvalidOperationException("本地播放标识与现有来源冲突。");
            }
            return;
        }
        if (_library == null) throw new InvalidOperationException("代理配置不受当前播放路径支持或策略检查失败。");
        var context = await _library.GetProxyPolicyContextAsync(ct).ConfigureAwait(false);
        var resolution = ProxyPolicyResolver.ResolveChannel(channel, context.Sources, context.Proxies);
        if (resolution.Status != ProxyResolutionStatus.System)
            throw new InvalidOperationException("代理配置不受当前播放路径支持或策略检查失败。");
    }

    internal static void ValidateOriginHeaders(SourceConfig source)
    {
        if (source.Headers?.Any(h => h.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || h.Value == null || h.Key.Any(char.IsControl) || h.Value.Any(char.IsControl)) == true)
            throw new InvalidOperationException("来源请求头包含不支持的代理认证字段或无效值。");
    }
}
