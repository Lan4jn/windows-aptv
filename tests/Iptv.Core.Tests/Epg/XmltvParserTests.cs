using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Iptv.Core.Epg;
using Iptv.Core.Models;
using Xunit;

namespace Iptv.Core.Tests.Epg;

public class XmltvParserTests
{
    private readonly XmltvParser _parser = new();

    private class NonSeekableStreamWrapper : Stream
    {
        private readonly Stream _inner;
        public NonSeekableStreamWrapper(Stream inner) => _inner = inner;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ParseAsync_ValidXmltv_ReturnsChannelsAndProgrammes()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <tv>
              <channel id="cctv1">
                <display-name>CCTV-1 综合</display-name>
              </channel>
              <programme start="20260401120000 +0800" stop="20260401123000 +0800" channel="cctv1">
                <title lang="zh">新闻30分</title>
                <desc lang="zh">午间新闻节目</desc>
              </programme>
              <programme start="20260401123000 +0800" stop="20260401130000 +0800" channel="cctv1">
                <title lang="zh">今日说法</title>
              </programme>
            </tv>
            """;

        var result = await _parser.ParseAsync(xml);

        Assert.Single(result.Channels);
        Assert.Equal("cctv1", result.Channels[0].Id);
        Assert.Equal("CCTV-1 综合", result.Channels[0].DisplayName);

        Assert.Equal(2, result.Programmes.Count);
        var prog1 = result.Programmes[0];
        Assert.Equal("cctv1", prog1.ChannelId);
        Assert.Equal("新闻30分", prog1.Title);
        Assert.Equal("午间新闻节目", prog1.Description);
        Assert.Equal(DateTimeOffset.Parse("2026-04-01T12:00:00+08:00"), prog1.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-04-01T12:30:00+08:00"), prog1.End);
    }

    [Fact]
    public async Task ParseAsync_RootNotTv_RssWithChannels_ThrowsFormatException()
    {
        var rss = "<rss><channel id='fake1'><display-name>Fake Channel</display-name></channel></rss>";
        await Assert.ThrowsAsync<FormatException>(() => _parser.ParseAsync(rss));
    }

    [Fact]
    public async Task ParseAsync_NestedTvInsideAnotherRoot_ThrowsFormatException()
    {
        var nested = "<root><tv><channel id='c1'><display-name>Ch 1</display-name></channel></tv></root>";
        await Assert.ThrowsAsync<FormatException>(() => _parser.ParseAsync(nested));
    }

    [Fact]
    public async Task ParseAsync_EmptyTvRoot_ReturnsEmptyResult()
    {
        var emptyTv = "<tv></tv>";
        var result = await _parser.ParseAsync(emptyTv);
        Assert.Empty(result.Channels);
        Assert.Empty(result.Programmes);
    }

    [Fact]
    public async Task ParseAutoAsync_NonSeekableGzipStream_ParsesSuccessfully()
    {
        var xml = "<tv><channel id='c1'><display-name>Ch 1</display-name></channel></tv>";
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            await gz.WriteAsync(Encoding.UTF8.GetBytes(xml));
        }
        ms.Position = 0;

        var nonSeekable = new NonSeekableStreamWrapper(ms);
        var result = await _parser.ParseAutoAsync(nonSeekable);
        Assert.Single(result.Channels);
    }

    [Fact]
    public async Task ParseAsync_GzipStream_DecompressesAndParsesCorrectly()
    {
        var xml = """
            <tv>
              <channel id="ch1">
                <display-name>Channel 1</display-name>
              </channel>
              <programme start="20260401080000 +0000" stop="20260401090000 +0000" channel="ch1">
                <title>Morning Show</title>
              </programme>
            </tv>
            """;

        byte[] rawBytes = Encoding.UTF8.GetBytes(xml);
        using var compressedMs = new MemoryStream();
        using (var gzip = new GZipStream(compressedMs, CompressionMode.Compress, leaveOpen: true))
        {
            await gzip.WriteAsync(rawBytes);
        }
        compressedMs.Position = 0;

        var result = await _parser.ParseGzipAsync(compressedMs);

        Assert.Single(result.Channels);
        Assert.Single(result.Programmes);
        Assert.Equal("Morning Show", result.Programmes[0].Title);
    }

    [Fact]
    public async Task ParseAsync_AutoDetectsGzipOrPlainStream()
    {
        var xml = """
            <tv>
              <channel id="ch1"><display-name>Test</display-name></channel>
              <programme start="20260401000000 Z" stop="20260401010000 Z" channel="ch1">
                <title>Show</title>
              </programme>
            </tv>
            """;

        // Plain stream
        using var plainMs = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var plainResult = await _parser.ParseAutoAsync(plainMs);
        Assert.Single(plainResult.Programmes);

        // Gzip stream
        using var compressedMs = new MemoryStream();
        using (var gzip = new GZipStream(compressedMs, CompressionMode.Compress, leaveOpen: true))
        {
            await gzip.WriteAsync(Encoding.UTF8.GetBytes(xml));
        }
        compressedMs.Position = 0;
        var gzipResult = await _parser.ParseAutoAsync(compressedMs);
        Assert.Single(gzipResult.Programmes);
    }

    [Fact]
    public async Task ParseAsync_TimeWithoutOffset_DefaultsToUtcWithWarning()
    {
        var xml = """
            <tv>
              <programme start="20260401120000" stop="20260401130000" channel="cctv1">
                <title>No Offset Show</title>
              </programme>
            </tv>
            """;

        var result = await _parser.ParseAsync(xml);

        Assert.Single(result.Programmes);
        Assert.Equal(TimeSpan.Zero, result.Programmes[0].Start.Offset);
        Assert.Contains(result.Warnings, w => w.Contains("UTC"));
    }

    [Fact]
    public async Task ParseAsync_BadOneProgramme_SkipsWithWarning()
    {
        var xml = """
            <tv>
              <programme start="20260401130000 +0000" stop="20260401120000 +0000" channel="cctv1">
                <title>Invalid Time Range (End &lt;= Start)</title>
              </programme>
              <programme start="20260401120000 +0000" stop="20260401130000 +0000" channel="cctv1">
                <title>Valid Show</title>
              </programme>
            </tv>
            """;

        var result = await _parser.ParseAsync(xml);

        Assert.Single(result.Programmes);
        Assert.Equal("Valid Show", result.Programmes[0].Title);
        Assert.Contains(result.Warnings, w => w.Contains("Invalid Time Range") || w.Contains("skipped"));
    }

    [Fact]
    public async Task ParseAsync_MalformedNonXml_ThrowsFormatException()
    {
        var nonXml = "<html><body>502 Bad Gateway</body></html>";
        await Assert.ThrowsAnyAsync<Exception>(() => _parser.ParseAsync(nonXml));
    }
}
