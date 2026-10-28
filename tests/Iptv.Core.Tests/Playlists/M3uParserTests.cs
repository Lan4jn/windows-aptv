using System;
using System.Collections.Generic;
using System.IO;
using Iptv.Core.Models;
using Iptv.Core.Playlists;
using Xunit;

namespace Iptv.Core.Tests.Playlists;

public class M3uParserTests
{
    private readonly M3uParser _parser = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Not an M3U file\nsome text")]
    public void Parse_InvalidOrEmptyHeader_ThrowsM3uParseException(string invalidContent)
    {
        Assert.Throws<M3uParseException>(() => _parser.Parse(invalidContent, null, "source-1"));
    }

    [Fact]
    public void Parse_ValidM3uWithHeaderEpg_ExtractsChannelsAndEpgUrls()
    {
        var content = """
            #EXTM3U url-tvg="http://epg.example.com/epg.xml, http://epg2.example.com/epg.xml.gz"
            #EXTINF:-1 tvg-id="cctv1" tvg-name="CCTV-1" tvg-logo="http://logo.example.com/cctv1.png" group-title="News",CCTV 1 HD
            http://stream.example.com/cctv1.m3u8
            #EXTINF:0 group-title="Sports",CCTV 5+
            http://stream.example.com/cctv5.m3u8
            """;

        var result = _parser.Parse(content, null, "src-100");

        Assert.Equal(2, result.Channels.Count);
        Assert.Equal(2, result.EpgUrls.Count);
        Assert.Contains("http://epg.example.com/epg.xml", result.EpgUrls);
        Assert.Contains("http://epg2.example.com/epg.xml.gz", result.EpgUrls);

        var c1 = result.Channels[0];
        Assert.Equal("CCTV 1 HD", c1.Name);
        Assert.Equal("News", c1.Group);
        Assert.Equal("cctv1", c1.TvgId);
        Assert.Equal("CCTV-1", c1.TvgName);
        Assert.Equal("http://logo.example.com/cctv1.png", c1.LogoUrl);
        Assert.Equal("http://stream.example.com/cctv1.m3u8", c1.StreamUrl);
        Assert.Equal("src-100", c1.SourceId);

        var c2 = result.Channels[1];
        Assert.Equal("CCTV 5+", c2.Name);
        Assert.Equal("Sports", c2.Group);
    }

    [Fact]
    public void Parse_WithUtf8BomAndCrlf_ParsesSuccessfully()
    {
        var bom = "\uFEFF";
        var content = bom + "#EXTM3U\r\n#EXTINF:-1 group-title=\"Main\",Channel One\r\nhttp://example.com/1.m3u8\r\n";

        var result = _parser.Parse(content, null, "src-1");

        Assert.Single(result.Channels);
        Assert.Equal("Channel One", result.Channels[0].Name);
    }

    [Fact]
    public void Parse_CommaInChannelName_PreservesCommaCorrectly()
    {
        var content = """
            #EXTM3U
            #EXTINF:-1 tvg-id="ch1",News, World & Finance
            http://example.com/stream.m3u8
            """;

        var result = _parser.Parse(content, null, "src-1");

        Assert.Single(result.Channels);
        Assert.Equal("News, World & Finance", result.Channels[0].Name);
    }

    [Fact]
    public void Parse_RelativeStreamUrl_ResolvesAgainstBaseUri()
    {
        var content = """
            #EXTM3U
            #EXTINF:-1,Relative Channel
            live/stream.m3u8
            """;
        var baseUri = new Uri("http://server.example.com/playlists/index.m3u");

        var result = _parser.Parse(content, baseUri, "src-1");

        Assert.Single(result.Channels);
        Assert.Equal("http://server.example.com/playlists/live/stream.m3u8", result.Channels[0].StreamUrl);
    }

    [Fact]
    public void Parse_ChannelIdIsolation_DifferentiatesSameNameAcrossSources()
    {
        var content = """
            #EXTM3U
            #EXTINF:-1,Same Name
            http://example.com/stream.m3u8
            """;

        var resA = _parser.Parse(content, null, "source-A");
        var resB = _parser.Parse(content, null, "source-B");

        Assert.NotEqual(resA.Channels[0].Id, resB.Channels[0].Id);
        Assert.StartsWith("source-A", resA.Channels[0].Id);
        Assert.StartsWith("source-B", resB.Channels[0].Id);
    }

    [Fact]
    public void Parse_InvalidOrMissingStreamUrl_SkipsAndEmitsWarning()
    {
        var content = """
            #EXTM3U
            #EXTINF:-1,No Stream Channel
            #EXTINF:-1,Valid Channel
            http://example.com/valid.m3u8
            #EXTINF:-1,Invalid Scheme Channel
            javascript:alert(1)
            """;

        var result = _parser.Parse(content, null, "src-1");

        Assert.Single(result.Channels);
        Assert.Equal("Valid Channel", result.Channels[0].Name);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Parse_DefaultHeadersPassedToChannels()
    {
        var content = """
            #EXTM3U
            #EXTINF:-1,Header Channel
            http://example.com/header.m3u8
            """;
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = "APTV-Test/1.0",
            ["Referer"] = "http://example.com"
        };

        var result = _parser.Parse(content, null, "src-1", headers);

        Assert.Single(result.Channels);
        var ch = result.Channels[0];
        Assert.NotNull(ch.Headers);
        Assert.Equal("APTV-Test/1.0", ch.Headers!["User-Agent"]);
        Assert.Equal("http://example.com", ch.Headers!["Referer"]);
    }

    [Fact]
    public void Parse_RelativeEpgUrlsAndLogos_ResolvesAgainstBaseUri()
    {
        var content = """
            #EXTM3U url-tvg="../epg.xml"
            #EXTINF:-1 tvg-logo="logos/cctv1.png",CCTV 1
            http://example.com/stream.m3u8
            """;
        var baseUri = new Uri("http://example.com/playlists/channel_list.m3u");

        var result = _parser.Parse(content, baseUri, "src-1");

        Assert.Single(result.EpgUrls);
        Assert.Equal("http://example.com/epg.xml", result.EpgUrls[0]);
        Assert.Equal("http://example.com/playlists/logos/cctv1.png", result.Channels[0].LogoUrl);
    }

    [Fact]
    public void Parse_QuotedAttributeWithComma_CorrectlySeparatesTitle()
    {
        var content = """
            #EXTM3U
            #EXTINF:-1 group-title="News, HD" tvg-id="cctv1",CCTV 1, Special Edition
            http://example.com/stream.m3u8
            """;

        var result = _parser.Parse(content, null, "src-1");

        Assert.Single(result.Channels);
        var ch = result.Channels[0];
        Assert.Equal("News, HD", ch.Group);
        Assert.Equal("cctv1", ch.TvgId);
        Assert.Equal("CCTV 1, Special Edition", ch.Name);
    }

    [Fact]
    public void Parse_EmptyPlaylistWithValidHeader_EmitsEmptyWarning()
    {
        var content = """
            #EXTM3U
            # Just comments
            """;

        var result = _parser.Parse(content, null, "src-1");

        Assert.Empty(result.Channels);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("0") || w.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }
}
