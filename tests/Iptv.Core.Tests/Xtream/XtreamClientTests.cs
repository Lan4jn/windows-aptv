using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Models;
using Iptv.Core.Xtream;
using Xunit;

namespace Iptv.Core.Tests.Xtream;

public class XtreamClientTests
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
    public void BaseUrl_Validation_RequiresAbsoluteHttpOrHttps_RejectsUserInfoQueryFragment()
    {
        using var client = new HttpClient();

        // 1. Rejects relative url
        Assert.Throws<ArgumentException>(() => new XtreamClient(client, "/relative/path", "user", "pass"));
        Assert.Throws<ArgumentException>(() => new XtreamClient(client, "example.com:8080", "user", "pass"));

        // 2. Rejects ftp / file schemes
        Assert.Throws<ArgumentException>(() => new XtreamClient(client, "ftp://example.com", "user", "pass"));

        // 3. Rejects userinfo in base url
        Assert.Throws<ArgumentException>(() => new XtreamClient(client, "http://admin:secret@example.com", "user", "pass"));

        // 4. Rejects query or fragment in base url
        Assert.Throws<ArgumentException>(() => new XtreamClient(client, "http://example.com?query=1", "user", "pass"));
        Assert.Throws<ArgumentException>(() => new XtreamClient(client, "http://example.com#section", "user", "pass"));

        // 5. Allows valid base with subpath
        var validClient = new XtreamClient(client, "http://example.com:8080/xtream-subpath/", "user", "pass");
        Assert.StartsWith("http://example.com:8080/xtream-subpath", validClient.GetXmltvUrl());
    }

    [Fact]
    public async Task AuthenticateAsync_ValidAccount_ReturnsAccountInfo()
    {
        var jsonResponse = """
            {
              "user_info": {
                "username": "user1",
                "password": "pass1",
                "message": "Auth success",
                "auth": 1,
                "status": "Active",
                "exp_date": "1893456000",
                "is_trial": "0",
                "active_cons": "0",
                "max_connections": "1"
              },
              "server_info": {
                "url": "example.com",
                "port": "80",
                "https_port": "443",
                "server_protocol": "http"
              }
            }
            """;

        var handler = new FakeHttpHandler(req =>
        {
            Assert.Contains("username=user1", req.RequestUri?.Query ?? "");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var xtream = new XtreamClient(client, "http://xtream.local", "user1", "pass1");

        var account = await xtream.AuthenticateAsync();

        Assert.Equal("user1", account.Username);
        Assert.Equal("Active", account.Status);
        Assert.True(account.IsAuthenticated);
        Assert.NotNull(account.ExpirationDate);
    }

    [Fact]
    public async Task AuthenticateAsync_AuthZero_ThrowsXtreamAuthException()
    {
        var jsonResponse = """
            {
              "user_info": {
                "username": "user1",
                "auth": 0,
                "status": "Active"
              }
            }
            """;

        var handler = new FakeHttpHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
        });

        using var client = new HttpClient(handler);
        var xtream = new XtreamClient(client, "http://xtream.local", "user1", "wrongpass");

        var ex = await Assert.ThrowsAsync<XtreamAuthException>(() => xtream.AuthenticateAsync());
        Assert.Contains("Authentication failed", ex.Message);
    }

    [Fact]
    public async Task AuthenticateAsync_DisabledAccount_ThrowsXtreamAuthException()
    {
        var jsonResponse = """
            {
              "user_info": {
                "username": "user1",
                "auth": 1,
                "status": "Disabled"
              }
            }
            """;

        var handler = new FakeHttpHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
        });

        using var client = new HttpClient(handler);
        var xtream = new XtreamClient(client, "http://xtream.local", "user1", "pass1");

        var ex = await Assert.ThrowsAsync<XtreamAuthException>(() => xtream.AuthenticateAsync());
        Assert.Contains("Disabled", ex.Message);
    }

    [Fact]
    public async Task AuthenticateAsync_StatusExpiredWithNullExpDate_ThrowsXtreamAuthException()
    {
        var jsonResponse = """
            {
              "user_info": {
                "username": "user1",
                "auth": 1,
                "status": "Expired",
                "exp_date": null
              }
            }
            """;

        var handler = new FakeHttpHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
        });

        using var client = new HttpClient(handler);
        var xtream = new XtreamClient(client, "http://xtream.local", "user1", "pass1");

        var ex = await Assert.ThrowsAsync<XtreamAuthException>(() => xtream.AuthenticateAsync());
        Assert.Contains("Expired", ex.Message);
    }

    [Fact]
    public async Task Security_ExceptionToStringAndInnerException_NeverContainPassword()
    {
        const string secretPass = "SuperSecretPassword123!#$";

        var handler = new FakeHttpHandler(req =>
        {
            throw new HttpRequestException($"Connection to {req.RequestUri} refused");
        });

        using var client = new HttpClient(handler);
        var xtream = new XtreamClient(client, "http://xtream.local", "user1", secretPass);

        var ex = await Assert.ThrowsAnyAsync<XtreamException>(() => xtream.AuthenticateAsync());

        // 1. Message must not leak password
        Assert.DoesNotContain(secretPass, ex.Message);

        // 2. ToString() must not leak password
        Assert.DoesNotContain(secretPass, ex.ToString());

        // 3. InnerException must be null (no raw leak)
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task UrlEncoding_SpecialCharactersInCredentials_ProperlyPercentEncoded()
    {
        string capturedQuery = "";

        var handler = new FakeHttpHandler(req =>
        {
            capturedQuery = req.RequestUri?.Query ?? "";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"user_info\":{\"auth\":1,\"status\":\"Active\"}}", Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var xtream = new XtreamClient(client, "http://xtream.local", "user name@test", "pass+word&key=1#");

        await xtream.AuthenticateAsync();

        Assert.Contains("user%20name%40test", capturedQuery);
        Assert.Contains("pass%2Bword%26key%3D1%23", capturedQuery);
    }

    [Fact]
    public async Task GetLiveStreamsAsync_MapsToChannels_WithFlexibleJsonAndDuplicateCategories()
    {
        var categoriesJson = """
            [
              { "category_id": "1", "category_name": "Sports" },
              { "category_id": 1, "category_name": "Sports Duplicate" },
              { "category_id": "2", "category_name": "News" }
            ]
            """;

        var streamsJson = """
            [
              {
                "num": "1",
                "name": "CCTV 5 HD",
                "stream_type": "live",
                "stream_id": "1001",
                "stream_icon": "http://logo.com/cctv5.png",
                "epg_channel_id": "cctv5_epg",
                "category_id": 1
              },
              {
                "num": 2,
                "name": null,
                "stream_type": "live",
                "stream_id": 1002,
                "stream_icon": null,
                "epg_channel_id": null,
                "category_id": "2"
              }
            ]
            """;

        var handler = new FakeHttpHandler(req =>
        {
            if (req.RequestUri!.Query.Contains("get_live_categories"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(categoriesJson) };
            }
            if (req.RequestUri!.Query.Contains("get_live_streams"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(streamsJson) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        var xtream = new XtreamClient(client, "http://xtream.local:8080/xtream", "user1", "pass1");

        var channels = await xtream.GetLiveChannelsAsync(sourceId: "src-xtream-1");

        Assert.Equal(2, channels.Count);

        var ch1 = channels[0];
        Assert.Equal("src-xtream-1:xtream:1001", ch1.Id);
        Assert.Equal("src-xtream-1", ch1.SourceId);
        Assert.Equal("CCTV 5 HD", ch1.Name);
        Assert.Equal("Sports", ch1.Group);
        Assert.Equal("http://xtream.local:8080/xtream/live/user1/pass1/1001.ts", ch1.StreamUrl);
        Assert.Equal("cctv5_epg", ch1.TvgId);
        Assert.Equal("http://logo.com/cctv5.png", ch1.LogoUrl);

        var ch2 = channels[1];
        Assert.Equal("src-xtream-1:xtream:1002", ch2.Id);
        Assert.Equal("Channel 1002", ch2.Name);
        Assert.Equal("News", ch2.Group);
    }

    [Fact]
    public async Task CustomHeaders_SentWithEveryRequest()
    {
        string capturedHeader = "";

        var handler = new FakeHttpHandler(req =>
        {
            if (req.Headers.TryGetValues("X-Custom-Client", out var values))
            {
                capturedHeader = string.Join(",", values);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"user_info\":{\"auth\":1,\"status\":\"Active\"}}")
            };
        });

        using var client = new HttpClient(handler);
        var headers = new Dictionary<string, string> { { "X-Custom-Client", "AptvWindows/1.0" } };
        var xtream = new XtreamClient(client, "http://xtream.local", "user1", "pass1", headers);

        await xtream.AuthenticateAsync();

        Assert.Equal("AptvWindows/1.0", capturedHeader);
    }

    [Fact]
    public async Task Cancellation_CallerCancelled_RethrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Already cancelled

        using var client = new HttpClient(new FakeHttpHandler(req => new HttpResponseMessage(HttpStatusCode.OK)));
        var xtream = new XtreamClient(client, "http://xtream.local", "user1", "pass1");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => xtream.AuthenticateAsync(cts.Token));
    }

    [Fact]
    public async Task Timeout_HttpClientTimeout_ThrowsXtreamTimeoutException()
    {
        // When HttpClient times out internally (not by user token), TaskCanceledException is thrown
        var handler = new FakeHttpHandler(req => throw new TaskCanceledException("HttpClient timeout"));

        using var client = new HttpClient(handler);
        var xtream = new XtreamClient(client, "http://xtream.local", "user1", "pass1");

        // When caller token was NOT cancelled, timeout must be reported cleanly as XtreamException
        var ex = await Assert.ThrowsAsync<XtreamException>(() => xtream.AuthenticateAsync(CancellationToken.None));
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetXmltvUrl_ReturnsProperUrl_AndMaskedDisplay()
    {
        var xtream = new XtreamClient(new HttpClient(), "http://xtream.local:8080", "user1", "secret123");

        var xmltvUrl = xtream.GetXmltvUrl();
        Assert.Equal("http://xtream.local:8080/xmltv.php?username=user1&password=secret123", xmltvUrl);

        var maskedUrl = xtream.GetMaskedXmltvUrl();
        Assert.Equal("http://xtream.local:8080/xmltv.php?username=user1&password=***", maskedUrl);
    }
}
