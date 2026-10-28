using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Security;

namespace Iptv.Desktop.Security;

/// <summary>
/// Windows DPAPI implementation of ICredentialStorage.
/// Encrypts source passwords per CurrentUser and stores them in isolated credential files.
/// </summary>
public sealed class WindowsCredentialStorage : ICredentialStorage
{
    private readonly string _credentialsDir;

    public WindowsCredentialStorage(string appDataDirectory)
    {
        _credentialsDir = Path.Combine(appDataDirectory, "credentials");
    }

    private string GetFilePath(string sourceId)
    {
        var safeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceId)));
        return Path.Combine(_credentialsDir, $"{safeId}.cred");
    }

    public Task SavePasswordAsync(string sourceId, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(password);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("WindowsCredentialStorage 仅在 Windows 环境下可用。");
        }

        Directory.CreateDirectory(_credentialsDir);
        var bytes = Encoding.UTF8.GetBytes(password);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GetFilePath(sourceId), encrypted);
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("WindowsCredentialStorage 仅在 Windows 环境下可用。");
        }

        var path = GetFilePath(sourceId);
        if (!File.Exists(path)) return Task.FromResult<string?>(null);

        try
        {
            var encrypted = File.ReadAllBytes(path);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(decrypted));
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task DeletePasswordAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        var path = GetFilePath(sourceId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Session-only memory implementation of ICredentialStorage for Mac development.
/// Retains passwords in memory without writing plaintext to disk.
/// </summary>
public sealed class SessionMemoryCredentialStorage : ICredentialStorage
{
    private readonly ConcurrentDictionary<string, string> _passwords = new(StringComparer.Ordinal);

    public Task SavePasswordAsync(string sourceId, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(password);
        _passwords[sourceId] = password;
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        _passwords.TryGetValue(sourceId, out var pwd);
        return Task.FromResult(pwd);
    }

    public Task DeletePasswordAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        _passwords.TryRemove(sourceId, out _);
        return Task.CompletedTask;
    }
}
