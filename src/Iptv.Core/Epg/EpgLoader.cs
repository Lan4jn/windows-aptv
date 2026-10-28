using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Iptv.Core.Epg;

public sealed class EpgLoader
{
    private readonly HttpClient _httpClient;
    private readonly XmltvParser _parser;
    private readonly ConcurrentDictionary<string, (EpgGuide Guide, DateTimeOffset Loaded)> _cache = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly object _cacheGate = new();
    private long _cacheGeneration;
    private readonly TimeSpan _cacheLifetime;

    public EpgLoader(HttpClient httpClient, XmltvParser? parser = null, TimeProvider? clock = null, TimeSpan? cacheLifetime = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _parser = parser ?? new XmltvParser();
        _clock = clock ?? TimeProvider.System;
        _cacheLifetime = cacheLifetime ?? TimeSpan.FromMinutes(15);
    }

    /// <summary>
    /// Loads and parses an EPG guide from a URL, utilizing in-memory cache if available.
    /// </summary>
    public async Task<EpgGuide> LoadGuideAsync(string url, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("EPG URL cannot be empty", nameof(url));

        cancellationToken.ThrowIfCancellationRequested();
        if (!forceRefresh && _cache.TryGetValue(url, out var cached) && _clock.GetUtcNow() - cached.Loaded < _cacheLifetime)
        {
            return cached.Guide;
        }

        long generation;
        lock (_cacheGate)
        {
            generation = ++_cacheGeneration;
            if (forceRefresh) _cache.TryRemove(url, out _);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        if (response.Content.Headers.ContentLength > _parser.MaxBufferBytes)
            throw new InvalidOperationException("EPG download exceeds the safety limit.");

        // Copy to MemoryStream to allow auto detection and seeking
        using var ms = new MemoryStream();
        await XmltvParser.CopyBoundedAsync(stream, ms, _parser.MaxBufferBytes, cancellationToken).ConfigureAwait(false);
        ms.Position = 0;

        var result = await _parser.ParseAutoAsync(ms, cancellationToken).ConfigureAwait(false);
        var guide = new EpgGuide(result.Programmes, result.Channels);

        cancellationToken.ThrowIfCancellationRequested();
        lock (_cacheGate)
        {
            if (generation == _cacheGeneration)
            {
                if (_cache.Count >= 16) _cache.Clear();
                _cache[url] = (guide, _clock.GetUtcNow());
            }
        }
        return guide;
    }

    /// <summary>
    /// Clears the in-memory EPG guide cache.
    /// </summary>
    public void ClearCache()
    {
        lock (_cacheGate)
        {
            ++_cacheGeneration;
            _cache.Clear();
        }
    }
}
