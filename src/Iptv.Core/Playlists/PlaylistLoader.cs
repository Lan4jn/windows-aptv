using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Models;

namespace Iptv.Core.Playlists;

/// <summary>
/// Loads M3U playlists from remote HTTP/HTTPS endpoints or local file system.
/// </summary>
public class PlaylistLoader
{
    private readonly HttpClient _httpClient;
    private readonly M3uParser _parser;

    public PlaylistLoader(HttpClient? httpClient = null, M3uParser? parser = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _parser = parser ?? new M3uParser();
    }

    public async Task<M3uParseResult> LoadAsync(SourceConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Kind != SourceKind.M3u)
        {
            throw new ArgumentException($"SourceConfig kind must be M3u, but got {config.Kind}", nameof(config));
        }

        string location = config.Location.Trim();

        if (Uri.TryCreate(location, UriKind.Absolute, out var absUri))
        {
            if (string.Equals(absUri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(absUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                return await LoadHttpAsync(config, absUri, cancellationToken);
            }

            if (string.Equals(absUri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                return await LoadFileAsync(config, absUri.LocalPath, cancellationToken);
            }

            // Sanitized scheme error without leaking query parameters or tokens in location
            throw new NotSupportedException(
                $"Unsupported playlist location scheme '{absUri.Scheme}'. Only http, https, and file schemes are supported.");
        }

        // Relative path or local file path
        if (location.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("ftp:", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Unsupported playlist location format.");
        }

        return await LoadFileAsync(config, location, cancellationToken);
    }

    private async Task<M3uParseResult> LoadHttpAsync(SourceConfig config, Uri locationUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, locationUri);

        ApplyHeaders(request, config.Headers);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return _parser.Parse(content, locationUri, config.Id, config.Headers);
    }

    private async Task<M3uParseResult> LoadFileAsync(SourceConfig config, string filePath, CancellationToken cancellationToken)
    {
        if (filePath.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(filePath, UriKind.Absolute, out var fileUri))
        {
            filePath = fileUri.LocalPath;
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("M3U playlist file not found.", filePath);
        }

        var content = await File.ReadAllTextAsync(filePath, System.Text.Encoding.UTF8, cancellationToken);
        var baseUri = new Uri(Path.GetFullPath(filePath));

        return _parser.Parse(content, baseUri, config.Id, config.Headers);
    }

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers == null || headers.Count == 0) return;

        foreach (var (rawKey, rawValue) in headers)
        {
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                throw new ArgumentException("Header name cannot be null or whitespace.");
            }

            // Guard against CRLF injection in header keys or values
            if (rawKey.Contains('\r') || rawKey.Contains('\n') ||
                rawValue.Contains('\r') || rawValue.Contains('\n'))
            {
                throw new ArgumentException("Header name or value cannot contain newline characters.");
            }

            string key = rawKey.Trim();
            string val = rawValue.Trim();

            if (string.Equals(key, "User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    request.Headers.UserAgent.Clear();
                    request.Headers.UserAgent.ParseAdd(val);
                }
                catch
                {
                    throw new ArgumentException("Invalid User-Agent header value.");
                }
            }
            else if (string.Equals(key, "Referer", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(val, UriKind.Absolute, out var refererUri) ||
                    (!string.Equals(refererUri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(refererUri.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
                {
                    // Static sanitized error message without echoing raw input which might carry sensitive tokens
                    throw new ArgumentException("Invalid Referer header; must be an absolute http or https URI.");
                }

                request.Headers.Referrer = refererUri;
            }
            else
            {
                if (!request.Headers.TryAddWithoutValidation(key, val))
                {
                    throw new ArgumentException($"Failed to add header '{key}'.");
                }
            }
        }
    }
}
