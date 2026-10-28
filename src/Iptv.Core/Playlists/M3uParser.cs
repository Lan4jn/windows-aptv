using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Iptv.Core.Models;

namespace Iptv.Core.Playlists;

/// <summary>
/// Parser for M3U and M3U8 playlists with support for IPTV extensions.
/// </summary>
public class M3uParser
{
    private static readonly Regex AttributeRegex = new(
        @"(?<key>[\w-]+)=(?:""(?<val>[^""]*)""|(?<val>[^\s,]+))",
        RegexOptions.Compiled);

    public M3uParseResult Parse(
        string content,
        Uri? baseUri,
        string sourceId,
        IReadOnlyDictionary<string, string>? defaultHeaders = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new M3uParseException("M3U content cannot be empty.");
        }

        // Strip UTF-8 BOM if present
        content = content.TrimStart('\uFEFF');

        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int startIndex = 0;

        // Skip leading blank lines
        while (startIndex < lines.Length && string.IsNullOrWhiteSpace(lines[startIndex]))
        {
            startIndex++;
        }

        if (startIndex >= lines.Length || !lines[startIndex].TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
        {
            throw new M3uParseException("M3U content must start with #EXTM3U header.");
        }

        var headerLine = lines[startIndex].Trim();
        var epgUrls = ExtractEpgUrlsFromHeader(headerLine, baseUri);
        var warnings = new List<string>();
        var channels = new List<Channel>();

        for (int i = startIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var (title, group, tvgId, tvgName, logoUrl) = ParseExtInf(line, baseUri);

                // Find next non-empty line which should be stream URL
                string? streamUrlLine = null;
                while (++i < lines.Length)
                {
                    var next = lines[i].Trim();
                    if (string.IsNullOrWhiteSpace(next)) continue;
                    if (next.StartsWith("#"))
                    {
                        if (next.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                        {
                            i--;
                            break;
                        }

                        // Check if it's #EXTGRP or another directive
                        if (next.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(group))
                        {
                            group = next["#EXTGRP:".Length..].Trim();
                        }
                        continue;
                    }
                    streamUrlLine = next;
                    break;
                }

                if (string.IsNullOrWhiteSpace(streamUrlLine))
                {
                    warnings.Add($"Channel '{title}' has no valid stream URL; skipped.");
                    continue;
                }

                // Resolve relative stream URL
                string? resolvedStreamUrl = ResolveStreamUrl(streamUrlLine, baseUri, out var schemeWarning);
                if (resolvedStreamUrl == null)
                {
                    warnings.Add($"Channel '{title}': {schemeWarning}");
                    continue;
                }

                string channelId = GenerateChannelId(sourceId, resolvedStreamUrl);

                var channel = new Channel(
                    id: channelId,
                    sourceId: sourceId,
                    name: string.IsNullOrWhiteSpace(title) ? (tvgName ?? tvgId ?? "Unknown Channel") : title,
                    streamUrl: resolvedStreamUrl,
                    group: group,
                    tvgId: tvgId,
                    tvgName: tvgName,
                    logoUrl: logoUrl,
                    headers: defaultHeaders);

                channels.Add(channel);
            }
        }

        if (channels.Count == 0)
        {
            warnings.Add("Playlist contains 0 valid channels; empty playlist source.");
        }

        return new M3uParseResult(channels, epgUrls, warnings);
    }

    private static List<string> ExtractEpgUrlsFromHeader(string headerLine, Uri? baseUri)
    {
        var urls = new List<string>();
        var matches = AttributeRegex.Matches(headerLine);
        foreach (Match match in matches)
        {
            var key = match.Groups["key"].Value;
            var val = match.Groups["val"].Value;
            if (string.Equals(key, "url-tvg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "x-tvg-url", StringComparison.OrdinalIgnoreCase))
            {
                var split = val.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var s in split)
                {
                    var trimmed = s.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        string resolved = ResolveUriString(trimmed, baseUri);
                        if (!urls.Contains(resolved))
                        {
                            urls.Add(resolved);
                        }
                    }
                }
            }
        }
        return urls;
    }

    private static (string title, string? group, string? tvgId, string? tvgName, string? logoUrl) ParseExtInf(string line, Uri? baseUri)
    {
        // Locate delimiter comma strictly outside quoted attribute values
        int commaIdx = -1;
        bool inQuotes = false;
        int searchStart = line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase) ? "#EXTINF:".Length : 0;

        for (int i = searchStart; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                commaIdx = i;
                break;
            }
        }

        string metadataPart = commaIdx >= 0 ? line[..commaIdx] : line;
        string title = commaIdx >= 0 ? line[(commaIdx + 1)..].Trim() : string.Empty;

        string? group = null;
        string? tvgId = null;
        string? tvgName = null;
        string? logoUrl = null;

        var matches = AttributeRegex.Matches(metadataPart);
        foreach (Match match in matches)
        {
            var key = match.Groups["key"].Value.ToLowerInvariant();
            var val = match.Groups["val"].Value.Trim();

            switch (key)
            {
                case "tvg-id":
                    tvgId = val;
                    break;
                case "tvg-name":
                    tvgName = val;
                    break;
                case "tvg-logo":
                    logoUrl = ResolveUriString(val, baseUri);
                    break;
                case "group-title":
                    group = val;
                    break;
            }
        }

        return (title, group, tvgId, tvgName, logoUrl);
    }

    private static string ResolveUriString(string uriStr, Uri? baseUri)
    {
        if (Uri.TryCreate(uriStr, UriKind.Absolute, out var absUri))
        {
            return absUri.AbsoluteUri;
        }

        if (baseUri != null && Uri.TryCreate(baseUri, uriStr, out var resolvedUri))
        {
            return resolvedUri.AbsoluteUri;
        }

        return uriStr;
    }

    private static string? ResolveStreamUrl(string streamUrl, Uri? baseUri, out string? warning)
    {
        warning = null;

        if (Uri.TryCreate(streamUrl, UriKind.Absolute, out var absoluteUri))
        {
            if (string.Equals(absoluteUri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(absoluteUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(absoluteUri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                return absoluteUri.AbsoluteUri;
            }

            warning = $"Unsupported URI scheme '{absoluteUri.Scheme}' in '{streamUrl}'. Only http, https and file are allowed.";
            return null;
        }

        if (baseUri != null && Uri.TryCreate(baseUri, streamUrl, out var resolvedUri))
        {
            if (string.Equals(resolvedUri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resolvedUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resolvedUri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                return resolvedUri.AbsoluteUri;
            }

            warning = $"Resolved relative URL '{resolvedUri}' has unsupported scheme '{resolvedUri.Scheme}'.";
            return null;
        }

        warning = $"Stream URL '{streamUrl}' is relative and cannot be resolved without a valid base URI.";
        return null;
    }

    private static string GenerateChannelId(string sourceId, string streamUrl)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(streamUrl);
        var hash = Convert.ToHexString(sha256.ComputeHash(bytes))[..16].ToLowerInvariant();
        return $"{sourceId}_{hash}";
    }
}
