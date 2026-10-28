using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Iptv.Core.Models;
using Iptv.Desktop.Services;
using Xunit.Abstractions;

namespace Iptv.Desktop.Tests.Services;

public sealed class IndependentManagedProxyAcceptanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadingEventReentryInvalidatesPriorStart(bool newerPlay)
    {
        var starts=new List<string>();Task? nested=null;var entered=false;
        using var session=new LibVlcPlayerSession(c=>{starts.Add(c.Id);return true;},(_,_)=>Task.CompletedTask);
        session.StateChanged+=(_,state)=>
        {
            if(state!=PlayerState.Loading||entered)return;entered=true;
            nested=newerPlay?session.PlayAsync(Channel("new")):session.StopAsync();
        };
        await session.PlayAsync(Channel("old"));
        await (nested??throw new Exception("Loading event not observed"));
        Assert.DoesNotContain("old",starts);
        if(newerPlay){Assert.Equal(new[]{"new"},starts);Assert.Equal("new",session.CurrentChannel?.Id);}
        else{Assert.Empty(starts);Assert.Null(session.CurrentChannel);Assert.Equal(PlayerState.Stopped,session.State);}
        output.WriteLine($"Loading synchronous reentry newerPlay={newerPlay}; starts={string.Join(',',starts)}; oldStarts=0; scope=pending-preflight-not-native-events");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCorePlaylistService_Http407Authentication_RejectAndSuccessObserved(bool wrongPassword)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var challenges=0;var rejected=0;var authenticated=0;var originRequests=0;
        var expected="Proxy-Authorization: Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-user:synthetic-correct"));
        await using var origin=new Server(async(stream,ct)=>
        {
            var headers=await Headers(stream,ct);
            Interlocked.Increment(ref originRequests);
            Assert.DoesNotContain("Proxy-Authorization:",headers,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("User-Agent: Independent/1",headers,StringComparison.OrdinalIgnoreCase);
            var body="#EXTM3U\n#EXTINF:-1,Synthetic channel\nhttp://stream.invalid/synthetic\n";
            await Write(stream,$"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n"+body,ct);
        });
        await using var proxy=new Server(async(stream,ct)=>
        {
            var headers=await Headers(stream,ct);
            if(!headers.Split("\r\n").Any(h=>h.Equals(expected,StringComparison.OrdinalIgnoreCase)))
            {
                if(headers.Contains("Proxy-Authorization:",StringComparison.OrdinalIgnoreCase))Interlocked.Increment(ref rejected);
                else Interlocked.Increment(ref challenges);
                await Write(stream,"HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=independent-service\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",ct);return;
            }
            Interlocked.Increment(ref authenticated);
            Assert.StartsWith("GET http://origin.invalid:18473/",headers);
            using var upstream=new TcpClient();await upstream.ConnectAsync(IPAddress.Loopback,origin.Port,ct);
            // Fixture forwards a deliberately constructed origin request; this is not evidence of product header filtering.
            await Write(upstream.GetStream(),"GET /playlist HTTP/1.1\r\nHost: origin.invalid\r\nUser-Agent: Independent/1\r\nConnection: close\r\n\r\n",ct);
            await upstream.GetStream().CopyToAsync(stream,ct);
        });
        var library=new MemoryOnlyLibraryService();
        var source=new SourceConfig("s","synthetic",SourceKind.M3u,"http://origin.invalid:18473/playlist",headers:new Dictionary<string,string>{{"User-Agent","Independent/1"}});
        await library.SaveConfigAsync(source);
        await library.SaveUserProxyAsync(new UserProxy("p","synthetic",ProxyKind.Http,$"http://127.0.0.1:{proxy.Port}","synthetic-user",wrongPassword?"synthetic-rejected":"synthetic-correct"));
        await library.SetSourceProxyAsync(source.Id,"p");
        using var systemClient=new HttpClient(new PoisonHandler());
        var service=new CorePlaylistService(systemClient,proxyPolicyService:new CoreProxyPolicyService(library));
        if(wrongPassword)
        {
            var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>service.LoadChannelsAsync(source,timeout.Token));
            Assert.DoesNotContain("synthetic-rejected",error.ToString());Assert.True(rejected>0);Assert.Equal(0,authenticated);Assert.Equal(0,originRequests);
        }
        else{var result=await service.LoadChannelsAsync(source,timeout.Token);Assert.Single(result.Channels);Assert.True(authenticated>0);Assert.Equal(1,originRequests);}
        Assert.True(challenges>0);Assert.Equal(0,origin.Failures);Assert.Equal(0,proxy.Failures);
        output.WriteLine($"actual=CorePlaylistService; wrongPassword={wrongPassword}; challenges={challenges}; explicitAuthRejects={rejected}; authenticated={authenticated}; originRequests={originRequests}; protocolFailures={proxy.Failures}");
    }
    private static Channel Channel(string id)=>new(id,"s","synthetic","http://origin.invalid/stream");
    private sealed class PoisonHandler:HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>throw new Exception("Borrowed system client must not be reached");}
    private sealed class Server:IAsyncDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);private readonly CancellationTokenSource _stop=new();private readonly List<Task> _clients=new();private readonly Task _loop;private int _failures;
        public int Port{get;}public int Failures=>Volatile.Read(ref _failures);
        public Server(Func<NetworkStream,CancellationToken,Task> handler){_listener.Start();Port=((IPEndPoint)_listener.LocalEndpoint).Port;_loop=Loop(handler);}
        private async Task Loop(Func<NetworkStream,CancellationToken,Task> handler){try{while(!_stop.IsCancellationRequested){var client=await _listener.AcceptTcpClientAsync(_stop.Token);_clients.Add(Handle(client,handler));}}catch(OperationCanceledException)when(_stop.IsCancellationRequested){}catch(SocketException)when(_stop.IsCancellationRequested){}}
        private async Task Handle(TcpClient client,Func<NetworkStream,CancellationToken,Task> handler){using(client)try{await handler(client.GetStream(),_stop.Token);}catch(OperationCanceledException)when(_stop.IsCancellationRequested){}catch{Interlocked.Increment(ref _failures);}}
        public async ValueTask DisposeAsync(){_stop.Cancel();_listener.Stop();await _loop;await Task.WhenAll(_clients);_stop.Dispose();}
    }
    private static async Task<string> Headers(Stream stream,CancellationToken ct){var data=new List<byte>();var one=new byte[1];while(data.Count<16384){if(await stream.ReadAsync(one,ct)==0)throw new EndOfStreamException();data.Add(one[0]);if(data.Count>=4&&data[^4]==13&&data[^3]==10&&data[^2]==13&&data[^1]==10)return Encoding.ASCII.GetString(data.ToArray());}throw new InvalidDataException("Synthetic header limit");}
    private static Task Write(Stream stream,string text,CancellationToken ct)=>stream.WriteAsync(Encoding.UTF8.GetBytes(text),ct).AsTask();
}
