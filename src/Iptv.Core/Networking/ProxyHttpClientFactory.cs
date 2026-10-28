using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Iptv.Core.Networking;

public static class ProxyHttpClientFactory
{
    public static ProxyHttpClientLease Create(ProxyResolution resolution) => CreateCore(resolution, null);

    internal static ProxyHttpClientLease Create(ProxyResolution resolution, X509ChainPolicy certificateChainPolicy)
    {
        ArgumentNullException.ThrowIfNull(certificateChainPolicy);
        return CreateCore(resolution, certificateChainPolicy);
    }

    private static ProxyHttpClientLease CreateCore(ProxyResolution resolution, X509ChainPolicy? certificateChainPolicy)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.Status == ProxyResolutionStatus.Error)
            throw new InvalidOperationException($"Proxy policy error: {resolution.Error}.");
        if (resolution.Status == ProxyResolutionStatus.System)
            return new(new HttpClient(), ownsClient: true);

        var snapshot = resolution.Snapshot!;
        var proxy = new WebProxy(snapshot.Endpoint)
        {
            BypassProxyOnLocal = false,
            Credentials = snapshot.CreateCredentials()
        };
        var handler = new SocketsHttpHandler { UseProxy = true, Proxy = proxy };
        if (certificateChainPolicy != null)
            handler.SslOptions.CertificateChainPolicy = certificateChainPolicy;
        return new(new HttpClient(handler, disposeHandler: true), ownsClient: true);
    }

    public static ProxyHttpClientLease BorrowSystem(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new(client, ownsClient: false);
    }
}

public sealed class ProxyHttpClientLease : IDisposable
{
    private readonly bool _ownsClient;
    private int _disposed;
    public HttpClient Client { get; }

    internal ProxyHttpClientLease(HttpClient client, bool ownsClient)
    {
        Client = client;
        _ownsClient = ownsClient;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsClient) Client.Dispose();
    }
}
