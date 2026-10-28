using System;
using System.Collections.Generic;

namespace Iptv.Core.Models;

/// <summary>
/// Represents a playable channel item within an IPTV source.
/// </summary>
public sealed record Channel
{
    public string Id { get; init; }
    public string SourceId { get; init; }
    public string Name { get; init; }
    public string? Group { get; init; }
    public string StreamUrl { get; init; }
    public string? TvgId { get; init; }
    public string? TvgName { get; init; }
    public string? LogoUrl { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    /// <summary>Null inherits the source proxy; a non-null ID explicitly overrides it.</summary>
    public string? ProxyId { get; init; }

    public Channel(
        string id,
        string sourceId,
        string name,
        string streamUrl,
        string? group = null,
        string? tvgId = null,
        string? tvgName = null,
        string? logoUrl = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string? proxyId = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Channel Id cannot be null or whitespace", nameof(id));
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("SourceId cannot be null or whitespace", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name cannot be null or whitespace", nameof(name));
        if (string.IsNullOrWhiteSpace(streamUrl)) throw new ArgumentException("StreamUrl cannot be null or whitespace", nameof(streamUrl));

        Id = id;
        SourceId = sourceId;
        Name = name;
        StreamUrl = streamUrl;
        Group = group;
        TvgId = tvgId;
        TvgName = tvgName;
        LogoUrl = logoUrl;
        Headers = headers != null ? new Dictionary<string, string>(headers) : null;
        ProxyId = proxyId;
    }
}
