using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Models;
using Iptv.Core.Security;

namespace Iptv.Core.Library;

/// <summary>
/// Manages atomic storage, loading, corruption backup, and encryption protection
/// for user library state.
/// </summary>
public class LibraryStateStore
{
    private static readonly byte[] ProtectedHeader = Encoding.UTF8.GetBytes("IPTV_PROTECTED_V1\n");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _storageDirectory;
    private readonly ILibraryStateProtector? _protector;

    public string StateFilePath => Path.Combine(_storageDirectory, "library.json");

    public LibraryStateStore(string storageDirectory, ILibraryStateProtector? protector = null)
    {
        if (string.IsNullOrWhiteSpace(storageDirectory))
        {
            throw new ArgumentException("Storage directory cannot be null or whitespace", nameof(storageDirectory));
        }

        _storageDirectory = storageDirectory;
        _protector = protector;
    }

    public async Task SaveAsync(LibraryState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        state = NormalizeState(state, state.SchemaVersion);
        // A fresh store must also preserve files written by a newer application.
        if (File.Exists(StateFilePath)) await LoadAsync(cancellationToken);

        // Security shield: if state contains sensitive tokens/credentials/Xtream and protector is missing, refuse saving!
        if (HasSensitiveData(state) && _protector == null)
        {
            throw new InvalidOperationException(
                "Cannot persist sensitive source or stream credentials without an ILibraryStateProtector.");
        }

        Directory.CreateDirectory(_storageDirectory);

        var jsonString = _protector == null
            ? JsonSerializer.Serialize(state, JsonOptions)
            : JsonSerializer.Serialize(ProtectedLibraryPayload.FromState(state), JsonOptions);
        var jsonBytes = Encoding.UTF8.GetBytes(jsonString);

        byte[] payloadBytes;
        if (_protector != null)
        {
            var cipher = _protector.Protect(jsonBytes);
            payloadBytes = new byte[ProtectedHeader.Length + cipher.Length];
            Buffer.BlockCopy(ProtectedHeader, 0, payloadBytes, 0, ProtectedHeader.Length);
            Buffer.BlockCopy(cipher, 0, payloadBytes, ProtectedHeader.Length, cipher.Length);
        }
        else
        {
            payloadBytes = jsonBytes;
        }

        // Atomic write via unique temporary file with guaranteed cleanup on failure or cancellation
        string tempPath = Path.Combine(_storageDirectory, $"library_{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(tempPath, payloadBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, StateFilePath, overwrite: true);
        }
        finally
        {
            // If temp file remains (e.g. cancellation or exception before File.Move), safely remove it
            if (File.Exists(tempPath))
            {
                try
                {
                    var fullTemp = Path.GetFullPath(tempPath);
                    var fullStorage = Path.GetFullPath(_storageDirectory);
                    if (fullTemp.StartsWith(fullStorage, StringComparison.Ordinal) && fullTemp.EndsWith(".tmp", StringComparison.Ordinal))
                    {
                        File.Delete(fullTemp);
                    }
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
    }

    public async Task<LibraryState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(StateFilePath))
        {
            return new LibraryState();
        }

        // OperationCanceledException, UnauthorizedAccessException, and IOException propagate directly
        byte[] fileBytes = await File.ReadAllBytesAsync(StateFilePath, cancellationToken);
        if (fileBytes.Length == 0)
        {
            return new LibraryState();
        }

        if (IsProtectedPayload(fileBytes))
        {
            if (_protector == null)
            {
                // Missing protector is a configuration error; do NOT treat as corrupted data
                throw new InvalidOperationException("Library file is encrypted but no ILibraryStateProtector is configured.");
            }

            var cipher = new byte[fileBytes.Length - ProtectedHeader.Length];
            Buffer.BlockCopy(fileBytes, ProtectedHeader.Length, cipher, 0, cipher.Length);

            byte[] jsonBytes;
            try
            {
                jsonBytes = _protector.Unprotect(cipher);
            }
            catch (Exception ex)
            {
                CreateCorruptedBackup();
                throw new InvalidDataException("Failed to decrypt protected library state. Corrupted file was backed up.", ex);
            }

            try
            {
                var version = ReadSchemaVersion(jsonBytes);
                var payload = JsonSerializer.Deserialize<ProtectedLibraryPayload>(jsonBytes, JsonOptions);
                return NormalizeState(payload?.ToState() ?? new LibraryState(), version);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                CreateCorruptedBackup();
                throw new InvalidDataException("Failed to deserialize decrypted library state. Corrupted file was backed up.", ex);
            }
        }
        else
        {
            try
            {
                var version = ReadSchemaVersion(fileBytes);
                var state = JsonSerializer.Deserialize<LibraryState>(fileBytes, JsonOptions);
                return NormalizeState(state ?? new LibraryState(), version);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                CreateCorruptedBackup();
                throw new InvalidDataException("Failed to deserialize library state. Corrupted file was backed up.", ex);
            }
        }
    }

    private static int ReadSchemaVersion(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var version = 1;
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var seen = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals(nameof(LibraryState.SchemaVersion), StringComparison.OrdinalIgnoreCase)) continue;
                if (seen || !property.Value.TryGetInt32(out version)) throw new JsonException("Invalid library schema version.");
                seen = true;
            }
        }
        EnsureSupportedSchema(version);
        return version;
    }

    private static void EnsureSupportedSchema(int version)
    {
        if (version > LibraryState.CurrentSchemaVersion)
            throw new NotSupportedException($"Library schema version {version} is newer than this application supports.");
        if (version < 1) throw new JsonException("Invalid library schema version.");
    }

    private static LibraryState NormalizeState(LibraryState state, int version)
    {
        EnsureSupportedSchema(version);
        if (state.Sources == null || state.Favorites == null || state.RecentHistory == null || state.UserProxies == null
            || state.Sources.Any(s => s == null) || state.Favorites.Any(c => c == null)
            || state.RecentHistory.Any(r => r == null || r.Channel == null) || state.UserProxies.Any(p => p == null))
            throw new JsonException("Library collections contain invalid null entries.");
        var proxyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var proxy in state.UserProxies)
        {
            _ = new UserProxy(proxy.Id, proxy.Name, proxy.Kind, proxy.Endpoint, proxy.Username, proxy.Password);
            if (!proxyIds.Add(proxy.Id)) throw new InvalidOperationException("Duplicate proxy ID in library state.");
        }
        var references = state.Sources.Select(s => s.ProxyId)
            .Concat(state.Favorites.Select(c => c.ProxyId))
            .Concat(state.RecentHistory.Select(r => r.Channel.ProxyId));
        if (references.Any(id => id != null && !proxyIds.Contains(id)))
            throw new InvalidOperationException("Library state contains an unresolved proxy reference.");
        return version == 1
            ? state with { SchemaVersion = LibraryState.CurrentSchemaVersion, Sources = state.Sources.Select((source, index) => source with { Order = index }).ToArray() }
            : state with { SchemaVersion = LibraryState.CurrentSchemaVersion };
    }

    private void CreateCorruptedBackup()
    {
        try
        {
            if (File.Exists(StateFilePath))
            {
                string guidPrefix = Guid.NewGuid().ToString("N")[..8];
                string backupName = $"library.corrupt.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{guidPrefix}.bak";
                string backupPath = Path.Combine(_storageDirectory, backupName);
                File.Copy(StateFilePath, backupPath, overwrite: false);
            }
        }
        catch
        {
            // Best effort backup creation
        }
    }

    private static bool IsProtectedPayload(byte[] bytes)
    {
        if (bytes.Length < ProtectedHeader.Length) return false;
        for (int i = 0; i < ProtectedHeader.Length; i++)
        {
            if (bytes[i] != ProtectedHeader[i]) return false;
        }
        return true;
    }

    private static bool HasSensitiveData(LibraryState state)
    {
        foreach (var proxy in state.UserProxies)
        {
            if (!string.IsNullOrEmpty(proxy.Username) || !string.IsNullOrEmpty(proxy.Password)) return true;
            if (IsSensitiveUrl(proxy.Endpoint)) return true;
        }

        foreach (var src in state.Sources)
        {
            // All Xtream sources require protection regardless of whether Password is populated or JsonIgnored
            if (src.Kind == SourceKind.Xtream) return true;
            if (!string.IsNullOrEmpty(src.Password)) return true;
            if (HasSensitiveHeaders(src.Headers)) return true;
            if (IsSensitiveUrl(src.Location)) return true;
            if (IsSensitiveUrl(src.CustomEpgUrl)) return true;
        }

        foreach (var fav in state.Favorites)
        {
            if (IsSensitiveUrl(fav.StreamUrl)) return true;
            if (IsSensitiveUrl(fav.LogoUrl)) return true;
            if (HasSensitiveHeaders(fav.Headers)) return true;
        }

        foreach (var rec in state.RecentHistory)
        {
            if (IsSensitiveUrl(rec.Channel.StreamUrl)) return true;
            if (IsSensitiveUrl(rec.Channel.LogoUrl)) return true;
            if (HasSensitiveHeaders(rec.Channel.Headers)) return true;
        }

        return false;
    }

    private static bool IsSensitiveUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // Check URI UserInfo (e.g. http://username:password@server.com/...)
            if (!string.IsNullOrEmpty(uri.UserInfo)) return true;

            // Check path segments for standard Xtream patterns: /live/user/pass/id.ext
            var path = uri.AbsolutePath;
            if (IsXtreamPath(path)) return true;

            // Check query string parameters (including percent-encoded keys/values)
            if (!string.IsNullOrEmpty(uri.Query))
            {
                var query = WebUtility.UrlDecode(uri.Query).TrimStart('?');
                var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
                foreach (var pair in pairs)
                {
                    var eq = pair.IndexOf('=');
                    var key = eq >= 0 ? pair[..eq].Trim().ToLowerInvariant() : pair.Trim().ToLowerInvariant();
                    if (IsSensitiveKey(key)) return true;
                }
            }
        }
        else
        {
            // Relative URI or raw path check
            var decoded = WebUtility.UrlDecode(url);
            if (decoded.Contains('@')) return true;
            if (IsXtreamPath(decoded)) return true;

            var lower = decoded.ToLowerInvariant();
            if (lower.Contains("token=") ||
                lower.Contains("password=") ||
                lower.Contains("secret=") ||
                lower.Contains("auth=") ||
                lower.Contains("apikey=") ||
                lower.Contains("api_key="))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsXtreamPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var p = path.ToLowerInvariant();

        // Standard Xtream path format: /live/user/pass/id.ts or /movie/user/pass/id.mp4 or /series/user/pass/id.mp4
        if (p.StartsWith("/live/") || p.StartsWith("/movie/") || p.StartsWith("/series/") || p.StartsWith("/play/"))
        {
            var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // parts[0] is live/movie/series, parts[1] is user, parts[2] is pass, parts[3] is id
            if (parts.Length >= 4) return true;
        }

        return false;
    }

    private static bool HasSensitiveHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers == null || headers.Count == 0) return false;

        foreach (var key in headers.Keys)
        {
            var k = key.ToLowerInvariant();
            if (IsSensitiveKey(k)) return true;
        }

        return false;
    }

    private static bool IsSensitiveKey(string key)
    {
        return key.Contains("auth") ||
               key.Contains("token") ||
               key.Contains("secret") ||
               key.Contains("pass") ||
               key.Contains("key") ||
               key.Contains("cookie") ||
               key.Contains("credential");
    }
}
