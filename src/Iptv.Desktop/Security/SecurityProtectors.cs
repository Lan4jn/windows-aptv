using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Iptv.Core.Security;

namespace Iptv.Desktop.Security;

/// <summary>
/// Windows DPAPI protector for encrypting sensitive state payloads (URLs, headers, tokens, credentials).
/// Runs on Windows with CurrentUser scope. Throws PlatformNotSupportedException on non-Windows.
/// </summary>
public sealed class WindowsDpapiStateProtector : ILibraryStateProtector
{
    public bool IsHardwareBacked => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public string ProtectionDescription => IsHardwareBacked
        ? "Windows DPAPI (CurrentUser) 加密保护"
        : "平台不支持 (非 Windows 环境)";

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("Windows DPAPI 仅在 Windows 运行时可用。在开发或非 Windows 环境下请使用会话内存保护器。");
        }

        return ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("Windows DPAPI 仅在 Windows 运行时可用。");
        }

        return ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
    }
}

/// <summary>
/// Session-only memory protector for development/testing on macOS or Linux.
/// Protects state payloads in memory during session lifecycle without making false encryption claims.
/// </summary>
public sealed class SessionMemoryStateProtector : ILibraryStateProtector
{
    public bool IsHardwareBacked => false;
    public string ProtectionDescription => "会话内存保护 (Mac开发模式：未持久化加密，退出即清除)";

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        // Returns a copy to preserve immutability in memory
        var copy = new byte[plaintext.Length];
        Array.Copy(plaintext, copy, plaintext.Length);
        return copy;
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        var copy = new byte[ciphertext.Length];
        Array.Copy(ciphertext, copy, ciphertext.Length);
        return copy;
    }
}
