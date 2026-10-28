using System;
using System.Collections.Generic;
using System.Linq;
using Iptv.Core.Models;

namespace Iptv.Core.Epg;

public sealed class EpgGuide
{
    private readonly Dictionary<string, List<EpgProgramme>> _programmesByChannelId;
    private readonly IReadOnlyList<XmltvChannel> _channels;
    private readonly HashSet<string> _channelIds;

    public EpgGuide(IEnumerable<EpgProgramme> programmes, IEnumerable<XmltvChannel>? channels = null)
    {
        _programmesByChannelId = new Dictionary<string, List<EpgProgramme>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in programmes)
        {
            if (!_programmesByChannelId.TryGetValue(p.ChannelId, out var list))
            {
                list = new List<EpgProgramme>();
                _programmesByChannelId[p.ChannelId] = list;
            }
            list.Add(p);
        }

        // Sort each channel's programmes by start time
        foreach (var list in _programmesByChannelId.Values)
        {
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        _channels = channels?.ToList() ?? new List<XmltvChannel>();
        _channelIds = new HashSet<string>(_channels.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the current active programme and the immediate next programme for a given channel ID.
    /// </summary>
    public (EpgProgramme? Now, EpgProgramme? Next) GetNowNext(string channelId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(channelId) || !_programmesByChannelId.TryGetValue(channelId, out var progs))
        {
            return (null, null);
        }

        EpgProgramme? current = null;
        EpgProgramme? next = null;

        for (int i = 0; i < progs.Count; i++)
        {
            var p = progs[i];
            if (p.Start <= now && now < p.End)
            {
                current = p;
                if (i + 1 < progs.Count)
                {
                    next = progs[i + 1];
                }
                break;
            }
            else if (p.Start > now && current == null)
            {
                // Found a future programme when no programme is currently broadcasting
                next = p;
                break;
            }
        }

        return (current, next);
    }

    /// <summary>
    /// Matches a Channel instance to an XMLTV channel ID.
    /// Preference order:
    /// 1. TvgId exact match.
    /// 2. Display name match (OrdinalIgnoreCase) fallback when unique.
    /// Returns null if not matched or if ambiguous.
    /// </summary>
    public string? MatchChannelId(Channel channel)
    {
        if (channel == null) return null;

        // 1. TvgId exact match
        if (!string.IsNullOrWhiteSpace(channel.TvgId))
        {
            if (_channelIds.Contains(channel.TvgId) || _programmesByChannelId.ContainsKey(channel.TvgId))
            {
                return channel.TvgId;
            }
        }

        // 2. DisplayName match fallback
        var targetName = !string.IsNullOrWhiteSpace(channel.TvgName) ? channel.TvgName : channel.Name;
        if (string.IsNullOrWhiteSpace(targetName)) return null;

        var matchingChannels = _channels
            .Where(c => c.DisplayName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Must be uniquely matched
        if (matchingChannels.Count == 1)
        {
            return matchingChannels[0].Id;
        }

        return null;
    }

    /// <summary>
    /// Resolves the effective EPG URL based on priority:
    /// 1. Custom URL set on source config.
    /// 2. First valid url-tvg from M3U header.
    /// 3. Global default URL.
    /// </summary>
    public static string? SelectEpgUrl(string? customEpgUrl, IEnumerable<string>? m3uUrlTvgs, string? globalDefaultEpgUrl)
    {
        if (!string.IsNullOrWhiteSpace(customEpgUrl))
        {
            return customEpgUrl.Trim();
        }

        if (m3uUrlTvgs != null)
        {
            foreach (var url in m3uUrlTvgs)
            {
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url.Trim();
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(globalDefaultEpgUrl))
        {
            return globalDefaultEpgUrl.Trim();
        }

        return null;
    }
}
