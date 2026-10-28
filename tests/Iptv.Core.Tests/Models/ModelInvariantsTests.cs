using System;
using System.Collections.Generic;
using System.Text.Json;
using Iptv.Core.Models;
using Iptv.Core.Security;
using Xunit;

namespace Iptv.Core.Tests.Models;

public class ModelInvariantsTests
{
    [Fact]
    public void Channel_ValidParameters_InstantiatesWithExpectedProperties()
    {
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = "TestAgent/1.0",
            ["Referer"] = "https://example.com"
        };

        var channel = new Channel(
            id: "src1_cctv1",
            sourceId: "src1",
            name: "CCTV-1 综合",
            streamUrl: "https://stream.example.com/live.m3u8",
            group: "央视频道",
            tvgId: "cctv1",
            tvgName: "CCTV1",
            logoUrl: "https://logo.example.com/cctv1.png",
            headers: headers
        );

        Assert.Equal("src1_cctv1", channel.Id);
        Assert.Equal("src1", channel.SourceId);
        Assert.Equal("CCTV-1 综合", channel.Name);
        Assert.Equal("https://stream.example.com/live.m3u8", channel.StreamUrl);
        Assert.Equal("央视频道", channel.Group);
        Assert.Equal("cctv1", channel.TvgId);
        Assert.Equal("CCTV1", channel.TvgName);
        Assert.Equal("https://logo.example.com/cctv1.png", channel.LogoUrl);
        Assert.NotNull(channel.Headers);
        Assert.Equal("TestAgent/1.0", channel.Headers["User-Agent"]);
    }

    [Theory]
    [InlineData("", "src1", "Name", "https://stream.com")]
    [InlineData("   ", "src1", "Name", "https://stream.com")]
    [InlineData("id", "", "Name", "https://stream.com")]
    [InlineData("id", "src1", "", "https://stream.com")]
    [InlineData("id", "src1", "Name", "")]
    public void Channel_InvalidRequiredFields_ThrowsArgumentException(string id, string sourceId, string name, string streamUrl)
    {
        Assert.Throws<ArgumentException>(() => new Channel(id, sourceId, name, streamUrl));
    }

    [Fact]
    public void Channel_Headers_AreImmutablyCopied()
    {
        var headers = new Dictionary<string, string> { ["Key"] = "Original" };
        var channel = new Channel("id1", "src1", "Name", "https://stream.com", headers: headers);

        headers["Key"] = "Modified";

        Assert.Equal("Original", channel.Headers!["Key"]);
    }

    [Fact]
    public void SourceConfig_ValidParameters_Instantiates()
    {
        var config = new SourceConfig(
            id: "src_my_iptv",
            name: "我的测试订阅",
            kind: SourceKind.M3u,
            location: "https://example.com/playlist.m3u",
            customEpgUrl: "https://example.com/epg.xml.gz",
            username: "user123",
            password: "secret_password"
        );

        Assert.Equal("src_my_iptv", config.Id);
        Assert.Equal("我的测试订阅", config.Name);
        Assert.Equal(SourceKind.M3u, config.Kind);
        Assert.Equal("https://example.com/playlist.m3u", config.Location);
        Assert.Equal("https://example.com/epg.xml.gz", config.CustomEpgUrl);
        Assert.Equal("user123", config.Username);
        Assert.Equal("secret_password", config.Password);
    }

    [Fact]
    public void SourceConfig_Password_IsNotSerializedToJson()
    {
        var config = new SourceConfig(
            id: "src_xtream",
            name: "Xtream Server",
            kind: SourceKind.Xtream,
            location: "http://xtream.server:8080",
            username: "user",
            password: "very_sensitive_password"
        );

        var json = JsonSerializer.Serialize(config);

        Assert.DoesNotContain("very_sensitive_password", json);
        Assert.DoesNotContain("Password", json);
    }

    [Fact]
    public void EpgProgramme_ValidTimes_Instantiates()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddHours(1);

        var programme = new EpgProgramme(
            channelId: "cctv1",
            title: "新闻联播",
            start: start,
            end: end,
            description: "每天晚间新闻播报"
        );

        Assert.Equal("cctv1", programme.ChannelId);
        Assert.Equal("新闻联播", programme.Title);
        Assert.Equal(start, programme.Start);
        Assert.Equal(end, programme.End);
        Assert.Equal("每天晚间新闻播报", programme.Description);
    }

    [Fact]
    public void EpgProgramme_EndTimeBeforeOrEqualStartTime_ThrowsArgumentException()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddMinutes(-5);

        Assert.Throws<ArgumentException>(() => new EpgProgramme("cctv1", "Title", start, end));
    }

    private class MockProtector : ILibraryStateProtector
    {
        public byte[] Protect(byte[] plaintext) => (byte[])plaintext.Clone();
        public byte[] Unprotect(byte[] ciphertext) => (byte[])ciphertext.Clone();
    }

    [Fact]
    public void LibraryStateProtector_InterfaceContract_CanBeImplemented()
    {
        ILibraryStateProtector protector = new MockProtector();
        byte[] original = [1, 2, 3, 4];
        var protectedBytes = protector.Protect(original);
        var unprotectedBytes = protector.Unprotect(protectedBytes);

        Assert.Equal(original, unprotectedBytes);
    }
}
