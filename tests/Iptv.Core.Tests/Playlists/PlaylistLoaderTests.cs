using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Models;
using Iptv.Core.Playlists;
using Xunit;

namespace Iptv.Core.Tests.Playlists;

public class PlaylistLoaderTests
{
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public async Task LoadAsync_LocalFile_ParsesSuccessfully()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_playlist_{Guid.NewGuid():N}.m3u");
        var m3uContent = """
            #EXTM3U
            #EXTINF:-1 group-title="Local",Test Channel
            http://example.com/test.m3u8
            """;
        await File.WriteAllTextAsync(tempFile, m3uContent);

        try
        {
            var config = new SourceConfig("src-local", "Local Playlist", SourceKind.M3u, tempFile);
            var loader = new PlaylistLoader();

            var result = await loader.LoadAsync(config);

            Assert.Single(result.Channels);
            Assert.Equal("Test Channel", result.Channels[0].Name);
            Assert.Equal("Local", result.Channels[0].Group);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task LoadAsync_LocalFileNotFound_ThrowsFileNotFoundException()
    {
        var nonExistentFile = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.m3u");
        var config = new SourceConfig("src-missing", "Missing", SourceKind.M3u, nonExistentFile);
        var loader = new PlaylistLoader();

        await Assert.ThrowsAsync<FileNotFoundException>(() => loader.LoadAsync(config));
    }

    [Fact]
    public async Task LoadAsync_HttpUrl_SendsCustomHeadersAndParsesResult()
    {
        var receivedHeaders = new Dictionary<string, string>();
        var fakeHandler = new FakeHttpMessageHandler(req =>
        {
            if (req.Headers.UserAgent != null)
            {
                receivedHeaders["User-Agent"] = req.Headers.UserAgent.ToString();
            }
            if (req.Headers.Referrer != null)
            {
                receivedHeaders["Referer"] = req.Headers.Referrer.ToString();
            }

            var m3u = """
                #EXTM3U url-tvg="http://server.com/epg.xml"
                #EXTINF:-1,Remote Channel
                http://server.com/remote.m3u8
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(m3u)
            };
        });

        var httpClient = new HttpClient(fakeHandler);
        var loader = new PlaylistLoader(httpClient);

        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = "APTV-Win/1.0",
            ["Referer"] = "http://ref.example.com"
        };
        var config = new SourceConfig("src-http", "Remote M3U", SourceKind.M3u, "http://server.com/list.m3u", headers: headers);

        var result = await loader.LoadAsync(config);

        Assert.Single(result.Channels);
        Assert.Equal("Remote Channel", result.Channels[0].Name);
        Assert.Equal("http://server.com/epg.xml", result.EpgUrls[0]);
        Assert.Equal("APTV-Win/1.0", receivedHeaders["User-Agent"]);
        Assert.Equal("http://ref.example.com/", receivedHeaders["Referer"]);
    }

    [Fact]
    public async Task LoadAsync_NonM3uKind_ThrowsArgumentException()
    {
        var config = new SourceConfig("src-xtream", "Xtream Source", SourceKind.Xtream, "http://server.com:8080");
        var loader = new PlaylistLoader();

        await Assert.ThrowsAsync<ArgumentException>(() => loader.LoadAsync(config));
    }

    [Fact]
    public async Task LoadAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var fakeHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("#EXTM3U\n")
        });
        var loader = new PlaylistLoader(new HttpClient(fakeHandler));
        var config = new SourceConfig("src-1", "Test", SourceKind.M3u, "http://server.com/list.m3u");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.LoadAsync(config, cts.Token));
    }

    [Fact]
    public async Task LoadAsync_BadRefererHeaderWithToken_ThrowsSanitizedArgumentExceptionWithoutEchoingToken()
    {
        var fakeHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var loader = new PlaylistLoader(new HttpClient(fakeHandler));

        var sensitiveToken = "secret_session_token_xyz999";
        var headers = new Dictionary<string, string>
        {
            ["Referer"] = $"relative_path_not_http?token={sensitiveToken}"
        };
        var config = new SourceConfig("src-1", "Test", SourceKind.M3u, "http://server.com/list.m3u", headers: headers);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => loader.LoadAsync(config));

        // Exception message must NOT echo the raw sensitive token!
        Assert.DoesNotContain(sensitiveToken, ex.Message);
        if (ex.InnerException != null)
        {
            Assert.DoesNotContain(sensitiveToken, ex.InnerException.Message);
        }
    }

    [Theory]
    [InlineData("User-Agent", "Mozilla/5.0\r\nInjected: true")]
    [InlineData("X-Custom", "value\nNew-Header: bad")]
    public async Task LoadAsync_HeaderValueWithCrlf_ThrowsSanitizedArgumentException(string headerName, string headerValue)
    {
        var fakeHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var loader = new PlaylistLoader(new HttpClient(fakeHandler));

        var headers = new Dictionary<string, string>
        {
            [headerName] = headerValue
        };
        var config = new SourceConfig("src-1", "Test", SourceKind.M3u, "http://server.com/list.m3u", headers: headers);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => loader.LoadAsync(config));
        Assert.DoesNotContain("\r", ex.Message);
        Assert.DoesNotContain("\n", ex.Message);
    }

    [Fact]
    public async Task LoadAsync_HeaderKeyWithCrlf_ThrowsSanitizedArgumentException()
    {
        var fakeHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var loader = new PlaylistLoader(new HttpClient(fakeHandler));

        var headers = new Dictionary<string, string>
        {
            ["Bad\r\nKey"] = "safe_value"
        };
        var config = new SourceConfig("src-1", "Test", SourceKind.M3u, "http://server.com/list.m3u", headers: headers);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => loader.LoadAsync(config));
        Assert.DoesNotContain("\r", ex.Message);
        Assert.DoesNotContain("\n", ex.Message);
    }

    [Theory]
    [InlineData("ftp://ftp.server.com/playlist.m3u")]
    [InlineData("javascript:alert(1)")]
    [InlineData("gopher://server.com/1")]
    public async Task LoadAsync_UnsupportedScheme_ThrowsNotSupportedExceptionWithoutFileRead(string unsupportedLocation)
    {
        var loader = new PlaylistLoader();
        var config = new SourceConfig("src-1", "Test", SourceKind.M3u, unsupportedLocation);

        await Assert.ThrowsAsync<NotSupportedException>(() => loader.LoadAsync(config));
    }
}
