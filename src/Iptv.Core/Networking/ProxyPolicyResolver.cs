using System.Net;
using System.Text;
using Iptv.Core.Models;

namespace Iptv.Core.Networking;

public enum ProxyResolutionStatus { System, Resolved, Error }
public enum ProxyPolicyError
{
    None, ReferenceMissing, ReferenceInvalid, ReferenceAmbiguous, SourceMissing,
    SourceAmbiguous, EndpointInvalid, TransportUnsupported, CredentialsInvalid
}

public sealed class ProxySnapshot
{
    private readonly string? _username;
    private readonly string? _password;
    public string Id { get; }
    public ProxyKind Kind { get; }
    public Uri Endpoint { get; }

    internal ProxySnapshot(UserProxy proxy, Uri endpoint)
    {
        Id = proxy.Id;
        Kind = proxy.Kind;
        Endpoint = endpoint;
        _username = proxy.Username;
        _password = proxy.Password;
    }

    internal NetworkCredential? CreateCredentials() => _username == null
        ? null : new NetworkCredential(_username, _password);
    public override string ToString() => "ProxySnapshot { Resolved }";
}

public sealed class ProxyResolution
{
    public ProxyResolutionStatus Status { get; }
    public ProxyPolicyError Error { get; }
    public ProxySnapshot? Snapshot { get; }

    internal ProxyResolution(ProxyResolutionStatus status, ProxyPolicyError error = ProxyPolicyError.None, ProxySnapshot? snapshot = null)
    {
        Status = status;
        Error = error;
        Snapshot = snapshot;
    }

    public override string ToString() => $"ProxyResolution {{ Status = {Status}, Error = {Error} }}";
}

public static class ProxyPolicyResolver
{
    public static ProxyResolution ResolveSource(SourceConfig source, IReadOnlyList<UserProxy> proxies)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(proxies);
        return ResolveReference(source.ProxyId, proxies);
    }

    public static ProxyResolution ResolveChannel(Channel channel, IReadOnlyList<SourceConfig> sources, IReadOnlyList<UserProxy> proxies)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(proxies);
        if (channel.ProxyId != null) return ResolveReference(channel.ProxyId, proxies);
        var matches = sources.Where(s => s.Id == channel.SourceId).Take(2).ToArray();
        if (matches.Length == 0) return Failed(ProxyPolicyError.SourceMissing);
        if (matches.Length != 1) return Failed(ProxyPolicyError.SourceAmbiguous);
        return ResolveReference(matches[0].ProxyId, proxies);
    }

    private static ProxyResolution ResolveReference(string? id, IReadOnlyList<UserProxy> proxies)
    {
        if (id == null) return new(ProxyResolutionStatus.System);
        if (string.IsNullOrWhiteSpace(id)) return Failed(ProxyPolicyError.ReferenceInvalid);
        var matches = proxies.Where(p => p.Id == id).Take(2).ToArray();
        if (matches.Length == 0) return Failed(ProxyPolicyError.ReferenceMissing);
        if (matches.Length != 1) return Failed(ProxyPolicyError.ReferenceAmbiguous);
        var proxy = matches[0];
        if (!Enum.IsDefined(proxy.Kind)) return Failed(ProxyPolicyError.TransportUnsupported);
        if (!Uri.TryCreate(proxy.Endpoint, UriKind.Absolute, out var endpoint)
            || string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port < 1
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.AbsolutePath is not ("" or "/"))
            return Failed(ProxyPolicyError.EndpointInvalid);
        if ((proxy.Kind == ProxyKind.Http && endpoint.Scheme != "http")
            || (proxy.Kind == ProxyKind.Socks5 && endpoint.Scheme != "socks5"))
            return Failed(ProxyPolicyError.TransportUnsupported);
        if ((proxy.Username == null) != (proxy.Password == null)
            || (proxy.Username != null && string.IsNullOrEmpty(proxy.Username))
            || HasControl(proxy.Username) || HasControl(proxy.Password)
            || (proxy.Kind == ProxyKind.Socks5 && proxy.Username != null
                && (Encoding.UTF8.GetByteCount(proxy.Username) > 255 || Encoding.UTF8.GetByteCount(proxy.Password!) > 255)))
            return Failed(ProxyPolicyError.CredentialsInvalid);
        return new(ProxyResolutionStatus.Resolved, snapshot: new ProxySnapshot(proxy, endpoint));
    }

    private static bool HasControl(string? value) => value != null && value.Any(char.IsControl);
    private static ProxyResolution Failed(ProxyPolicyError error) => new(ProxyResolutionStatus.Error, error);
}
