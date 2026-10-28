using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Epg;
using Xunit;

namespace Iptv.Core.Tests.Epg;

public class EpgLoaderTests
{
    private class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public async Task LoadGuideAsync_GzipContentEncoding_LoadsWithoutDoubleDecompress()
    {
        var xml = """
            <tv>
              <channel id="c1"><display-name>Ch 1</display-name></channel>
              <programme start="20260401000000 Z" stop="20260401010000 Z" channel="c1">
                <title>Show 1</title>
              </programme>
            </tv>
            """;

        byte[] compressedBytes;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
            {
                gz.Write(Encoding.UTF8.GetBytes(xml));
            }
            compressedBytes = ms.ToArray();
        }

        var handler = new FakeHttpHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(compressedBytes)
            };
            resp.Content.Headers.ContentEncoding.Add("gzip");
            return resp;
        });

        using var client = new HttpClient(handler);
        var loader = new EpgLoader(client);

        var guide = await loader.LoadGuideAsync("http://fake.epg/epg.xml.gz");
        var (now, next) = guide.GetNowNext("c1", DateTimeOffset.Parse("2026-04-01T00:10:00Z"));

        Assert.NotNull(now);
        Assert.Equal("Show 1", now.Title);
    }

    [Fact]
    public async Task LoadGuideAsync_CachesResultInMemory_ReturnsCachedInstance()
    {
        int callCount = 0;
        var xml = "<tv><channel id='c1'><display-name>Ch 1</display-name></channel></tv>";

        var handler = new FakeHttpHandler(req =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            };
        });

        using var client = new HttpClient(handler);
        var loader = new EpgLoader(client);

        var guide1 = await loader.LoadGuideAsync("http://fake.epg/guide.xml");
        var guide2 = await loader.LoadGuideAsync("http://fake.epg/guide.xml");

        Assert.Same(guide1, guide2);
        Assert.Equal(1, callCount); // Second call used cache!

        loader.ClearCache();
        var guide3 = await loader.LoadGuideAsync("http://fake.epg/guide.xml");
        Assert.NotSame(guide1, guide3);
        Assert.Equal(2, callCount); // Third call fetched after cache cleared!
    }
    [Fact]
    public async Task ForceRefreshAndExpiry_FetchFreshGuide()
    {
        int count = 0;
        var clock = new TestClock();
        using var client = new HttpClient(new FakeHttpHandler(req => {
            count++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<tv/>") };
        }));
        var loader = new EpgLoader(client, clock: clock, cacheLifetime: TimeSpan.FromMinutes(15));
        var first = await loader.LoadGuideAsync("http://guide/xml");
        Assert.Same(first, await loader.LoadGuideAsync("http://guide/xml"));
        Assert.NotSame(first, await loader.LoadGuideAsync("http://guide/xml", forceRefresh: true));
        clock.Now += TimeSpan.FromMinutes(16);
        await loader.LoadGuideAsync("http://guide/xml");
        Assert.Equal(3, count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedDownload_IsRejected_WithOrWithoutContentLength(bool knownLength)
    {
        using var client = new HttpClient(new FakeHttpHandler(req => {
            HttpContent content = knownLength ? new ByteArrayContent(new byte[256]) : new StreamContent(new NonSeekStream(new byte[256]));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        var loader = new EpgLoader(client, new XmltvParser(128));
        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadGuideAsync("http://guide/xml"));
    }

    [Fact]
    public async Task GzipExpansion_IsBounded()
    {
        using var compressed = new MemoryStream();
        using (var gz = new GZipStream(compressed, CompressionMode.Compress, true))
            gz.Write(Encoding.UTF8.GetBytes("<tv>" + new string(' ', 1024) + "</tv>"));
        compressed.Position = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new XmltvParser(128).ParseGzipAsync(compressed));
    }

    [Fact]
    public async Task PlainXmlAndCancelledRead_AreBounded()
    {
        var parser = new XmltvParser(128);
        using var xml = new MemoryStream(Encoding.UTF8.GetBytes("<tv>" + new string(' ', 256) + "</tv>"));
        await Assert.ThrowsAsync<FormatException>(() => parser.ParseAsync(xml));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var small = new MemoryStream(Encoding.UTF8.GetBytes("<tv/>"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parser.ParseAutoAsync(small, cancelled.Token));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NonSeekStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
