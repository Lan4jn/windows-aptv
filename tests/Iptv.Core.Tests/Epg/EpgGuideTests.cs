using System;
using System.Collections.Generic;
using Iptv.Core.Epg;
using Iptv.Core.Models;
using Xunit;

namespace Iptv.Core.Tests.Epg;

public class EpgGuideTests
{
    [Fact]
    public void GetNowNext_MatchesActiveAndNextProgrammes()
    {
        var now = DateTimeOffset.Parse("2026-04-01T12:15:00+08:00");

        var progs = new List<EpgProgramme>
        {
            new("cctv1", "早间新闻", DateTimeOffset.Parse("2026-04-01T07:00:00+08:00"), DateTimeOffset.Parse("2026-04-01T08:00:00+08:00")),
            new("cctv1", "新闻30分", DateTimeOffset.Parse("2026-04-01T12:00:00+08:00"), DateTimeOffset.Parse("2026-04-01T12:30:00+08:00")),
            new("cctv1", "今日说法", DateTimeOffset.Parse("2026-04-01T12:30:00+08:00"), DateTimeOffset.Parse("2026-04-01T13:00:00+08:00")),
            new("cctv1", "晚间新闻", DateTimeOffset.Parse("2026-04-01T22:00:00+08:00"), DateTimeOffset.Parse("2026-04-01T22:30:00+08:00"))
        };

        var guide = new EpgGuide(progs);
        var (current, next) = guide.GetNowNext("cctv1", now);

        Assert.NotNull(current);
        Assert.Equal("新闻30分", current.Title);

        Assert.NotNull(next);
        Assert.Equal("今日说法", next.Title);
    }

    [Fact]
    public void MatchChannelId_TvgIdExactMatch_TakesPrecedence()
    {
        var channels = new List<XmltvChannel>
        {
            new("cctv1_hd", "CCTV-1 综合"),
            new("cctv1_sd", "CCTV 1")
        };

        var guide = new EpgGuide(Array.Empty<EpgProgramme>(), channels);

        // Channel has TvgId="cctv1_hd" but Name="CCTV 1" (which matches display-name of sd)
        var channel = new Channel("ch-1", "src-1", "CCTV 1", "http://stream/1", tvgId: "cctv1_hd");

        var matchedId = guide.MatchChannelId(channel);
        Assert.Equal("cctv1_hd", matchedId);
    }

    [Fact]
    public void MatchChannelId_DisplayNameFallback_MatchesWhenUnique()
    {
        var channels = new List<XmltvChannel>
        {
            new("hunan_hd", "湖南卫视"),
            new("zhejiang_hd", "浙江卫视")
        };

        var guide = new EpgGuide(Array.Empty<EpgProgramme>(), channels);

        // No TvgId, match by Name
        var channel = new Channel("ch-2", "src-1", "湖南卫视", "http://stream/2");

        var matchedId = guide.MatchChannelId(channel);
        Assert.Equal("hunan_hd", matchedId);
    }

    [Fact]
    public void MatchChannelId_DisplayNameAmbiguity_ReturnsNull()
    {
        var channels = new List<XmltvChannel>
        {
            new("cctv5_hd", "CCTV-5"),
            new("cctv5_4k", "CCTV-5") // Same display name!
        };

        var guide = new EpgGuide(Array.Empty<EpgProgramme>(), channels);

        var channel = new Channel("ch-3", "src-1", "CCTV-5", "http://stream/3");

        // When ambiguous, do NOT randomly pick, return null
        var matchedId = guide.MatchChannelId(channel);
        Assert.Null(matchedId);
    }

    [Fact]
    public void SelectEpgUrl_PriorityOrder_CustomThenM3uThenGlobal()
    {
        // 1. Custom has highest priority
        var selected1 = EpgGuide.SelectEpgUrl("http://custom.epg", new[] { "http://m3u.epg" }, "http://global.epg");
        Assert.Equal("http://custom.epg", selected1);

        // 2. M3U url-tvg is second
        var selected2 = EpgGuide.SelectEpgUrl(null, new[] { "http://m3u.epg" }, "http://global.epg");
        Assert.Equal("http://m3u.epg", selected2);

        // 3. Global default is fallback
        var selected3 = EpgGuide.SelectEpgUrl(null, null, "http://global.epg");
        Assert.Equal("http://global.epg", selected3);

        // 4. All null returns null
        var selected4 = EpgGuide.SelectEpgUrl(null, null, null);
        Assert.Null(selected4);
    }
}
