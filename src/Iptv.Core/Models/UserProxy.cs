using System;
using System.Text.Json.Serialization;

namespace Iptv.Core.Models;

public enum ProxyKind
{
    Http,
    Socks5
}

/// <summary>A named proxy configuration. Persistence does not activate network routing.</summary>
public sealed record UserProxy
{
    public string Id { get; init; }
    public string Name { get; init; }
    public ProxyKind Kind { get; init; }
    public string Endpoint { get; init; }
    [JsonIgnore]
    public string? Username { get; init; }
    [JsonIgnore]
    public string? Password { get; init; }

    public UserProxy(string id, string name, ProxyKind kind, string endpoint, string? username = null, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Proxy ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Proxy name is required.", nameof(name));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (kind == ProxyKind.Http && uri.Scheme != "http" && uri.Scheme != "https")
            || (kind == ProxyKind.Socks5 && uri.Scheme != "socks5"))
            throw new ArgumentException("Proxy endpoint must be an absolute URL matching its kind.", nameof(endpoint));
        Id = id;
        Name = name;
        Kind = kind;
        Endpoint = endpoint;
        Username = username;
        Password = password;
    }

    public override string ToString() => $"UserProxy {{ Id = {Id}, Kind = {Kind} }}";
}
