using System;
using System.Collections.Generic;
using System.Linq;
using Iptv.Core.Models;

namespace Iptv.Core.Library;

// This DTO is serialized only inside ILibraryStateProtector. Domain model JSON remains redacted.
internal sealed record ProtectedLibraryPayload
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<SourceConfig> Sources { get; init; } = Array.Empty<SourceConfig>();
    public IReadOnlyList<Channel> Favorites { get; init; } = Array.Empty<Channel>();
    public IReadOnlyList<RecentlyPlayedItem> RecentHistory { get; init; } = Array.Empty<RecentlyPlayedItem>();
    public IReadOnlyList<UserProxy> UserProxies { get; init; } = Array.Empty<UserProxy>();
    public Dictionary<string, string?> SourcePasswords { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, ProxyCredentials> ProxyAuthentication { get; init; } = new(StringComparer.Ordinal);

    public static ProtectedLibraryPayload FromState(LibraryState state) => new()
    {
        SchemaVersion = state.SchemaVersion,
        Sources = state.Sources,
        Favorites = state.Favorites,
        RecentHistory = state.RecentHistory,
        UserProxies = state.UserProxies,
        SourcePasswords = state.Sources.Where(s => s.Password != null).ToDictionary(s => s.Id, s => s.Password, StringComparer.Ordinal),
        ProxyAuthentication = state.UserProxies.Where(p => p.Username != null || p.Password != null)
            .ToDictionary(p => p.Id, p => new ProxyCredentials(p.Username, p.Password), StringComparer.Ordinal)
    };

    public LibraryState ToState()
    {
        if (Sources == null || UserProxies == null || SourcePasswords == null || ProxyAuthentication == null
            || Sources.Any(s => s == null) || UserProxies.Any(p => p == null) || ProxyAuthentication.Values.Any(a => a == null))
            throw new System.Text.Json.JsonException("Invalid protected library collections.");
        if (SourcePasswords.Keys.Any(id => !Sources.Any(s => s.Id == id))
            || ProxyAuthentication.Keys.Any(id => !UserProxies.Any(p => p.Id == id)))
            throw new System.Text.Json.JsonException("Protected credentials reference missing records.");
        return new LibraryState
        {
            SchemaVersion = SchemaVersion,
            Sources = Sources.Select(s => SourcePasswords.TryGetValue(s.Id, out var password) ? s with { Password = password } : s).ToArray(),
            Favorites = Favorites,
            RecentHistory = RecentHistory,
            UserProxies = UserProxies.Select(p => ProxyAuthentication.TryGetValue(p.Id, out var auth)
                ? p with { Username = auth.Username, Password = auth.Password } : p).ToArray()
        };
    }

    internal sealed record ProxyCredentials(string? Username, string? Password);
}
