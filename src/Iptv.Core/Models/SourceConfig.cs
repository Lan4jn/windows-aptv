using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Iptv.Core.Models;

/// <summary>
/// Represents the configuration and endpoint for an IPTV subscription or source.
/// </summary>
public sealed record SourceConfig
{
    public string Id { get; init; }
    public string Name { get; init; }
    public SourceKind Kind { get; init; }
    public string Location { get; init; }
    public string? CustomEpgUrl { get; init; }
    public string? Username { get; init; }

    /// <summary>
    /// In-memory password. Ignored in standard JSON serialization to avoid plain text leakage.
    /// </summary>
    [JsonIgnore]
    public string? Password { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public int Order { get; init; }
    public string? ProxyId { get; init; }

    public SourceConfig(
        string id,
        string name,
        SourceKind kind,
        string location,
        string? customEpgUrl = null,
        string? username = null,
        string? password = null,
        IReadOnlyDictionary<string, string>? headers = null,
        int order = 0,
        string? proxyId = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("SourceConfig Id cannot be null or whitespace", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name cannot be null or whitespace", nameof(name));
        if (string.IsNullOrWhiteSpace(location)) throw new ArgumentException("Location cannot be null or whitespace", nameof(location));

        Id = id;
        Name = name;
        Kind = kind;
        Location = location;
        CustomEpgUrl = customEpgUrl;
        Username = username;
        Password = password;
        Headers = headers != null ? new Dictionary<string, string>(headers) : null;
        Order = order;
        ProxyId = proxyId;
    }
}
