using System;
using System.Collections.Generic;
using Iptv.Core.Models;

namespace Iptv.Core.Playlists;

/// <summary>
/// Result of parsing an M3U playlist content.
/// </summary>
public sealed record M3uParseResult
{
    public IReadOnlyList<Channel> Channels { get; init; }
    public IReadOnlyList<string> EpgUrls { get; init; }
    public IReadOnlyList<string> Warnings { get; init; }

    public M3uParseResult(
        IReadOnlyList<Channel> channels,
        IReadOnlyList<string>? epgUrls = null,
        IReadOnlyList<string>? warnings = null)
    {
        Channels = channels ?? Array.Empty<Channel>();
        EpgUrls = epgUrls ?? Array.Empty<string>();
        Warnings = warnings ?? Array.Empty<string>();
    }
}
