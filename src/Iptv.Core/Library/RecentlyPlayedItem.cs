using System;
using Iptv.Core.Models;

namespace Iptv.Core.Library;

/// <summary>
/// Represents a channel playback history entry with timestamp.
/// </summary>
public sealed record RecentlyPlayedItem
{
    public Channel Channel { get; init; }
    public DateTimeOffset PlayedAt { get; init; }

    public RecentlyPlayedItem(Channel channel, DateTimeOffset playedAt)
    {
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        PlayedAt = playedAt;
    }
}
