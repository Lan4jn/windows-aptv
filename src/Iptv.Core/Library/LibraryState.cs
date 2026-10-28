using System;
using System.Collections.Generic;
using System.Linq;
using Iptv.Core.Models;

namespace Iptv.Core.Library;

/// <summary>
/// Root persistent state of user playlists, favorites, and playback history.
/// </summary>
public sealed record LibraryState
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public IReadOnlyList<UserProxy> UserProxies { get; init; } = Array.Empty<UserProxy>();
    public IReadOnlyList<SourceConfig> Sources { get; init; } = Array.Empty<SourceConfig>();
    public IReadOnlyList<Channel> Favorites { get; init; } = Array.Empty<Channel>();
    public IReadOnlyList<RecentlyPlayedItem> RecentHistory { get; init; } = Array.Empty<RecentlyPlayedItem>();

    public LibraryState() { }

    public LibraryState(
        int schemaVersion,
        IReadOnlyList<SourceConfig>? sources,
        IReadOnlyList<Channel>? favorites,
        IReadOnlyList<RecentlyPlayedItem>? recentHistory,
        IReadOnlyList<UserProxy>? userProxies = null)
    {
        SchemaVersion = schemaVersion;
        Sources = sources ?? Array.Empty<SourceConfig>();
        Favorites = favorites ?? Array.Empty<Channel>();
        RecentHistory = recentHistory ?? Array.Empty<RecentlyPlayedItem>();
        UserProxies = userProxies ?? Array.Empty<UserProxy>();
    }

    /// <summary>
    /// Adds or replaces a favorite channel, deduplicating by Channel.Id.
    /// </summary>
    public LibraryState WithFavorite(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var list = Favorites.Where(f => !string.Equals(f.Id, channel.Id, StringComparison.Ordinal)).ToList();
        list.Add(channel);
        return this with { Favorites = list };
    }

    /// <summary>
    /// Removes a favorite channel by its ID.
    /// </summary>
    public LibraryState WithoutFavorite(string channelId)
    {
        var list = Favorites.Where(f => !string.Equals(f.Id, channelId, StringComparison.Ordinal)).ToList();
        return this with { Favorites = list };
    }

    /// <summary>
    /// Records a played channel at the top of recent history, capped by maxLimit.
    /// </summary>
    public LibraryState WithRecentPlayed(Channel channel, DateTimeOffset playedAt, int maxLimit = 100)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var item = new RecentlyPlayedItem(channel, playedAt);
        var list = RecentHistory.Where(r => !string.Equals(r.Channel.Id, channel.Id, StringComparison.Ordinal)).ToList();
        list.Insert(0, item);
        if (list.Count > maxLimit)
        {
            list = list.Take(maxLimit).ToList();
        }
        return this with { RecentHistory = list };
    }
}
