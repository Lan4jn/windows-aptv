using System.Runtime.InteropServices;
using System.Text;
using Iptv.Desktop.Security;
using Xunit;

namespace Iptv.Desktop.Tests.Security;

public class LibraryStateProtectorTests
{
    [Fact]
    public void SessionOnlyProtector_RoundTripsDataInMemory()
    {
        var protector = new SessionMemoryStateProtector();
        var raw = Encoding.UTF8.GetBytes("secret_library_payload_with_credentials");

        var encrypted = protector.Protect(raw);
        var decrypted = protector.Unprotect(encrypted);

        Assert.Equal(raw, decrypted);
        Assert.False(protector.IsHardwareBacked);
        Assert.Contains("会话内存", protector.ProtectionDescription);
    }

    [Fact]
    public void WindowsDpapiProtector_OnlyRunsOnWindows_OrThrowsPlatformNotSupported()
    {
        var protector = new WindowsDpapiStateProtector();
        var raw = Encoding.UTF8.GetBytes("test_payload");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var enc = protector.Protect(raw);
            var dec = protector.Unprotect(enc);
            Assert.Equal(raw, dec);
            Assert.True(protector.IsHardwareBacked);
            Assert.Contains("DPAPI", protector.ProtectionDescription);
        }
        else
        {
            // On non-Windows (e.g. macOS dev host), Protect throws PlatformNotSupportedException
            Assert.Throws<PlatformNotSupportedException>(() => protector.Protect(raw));
            Assert.False(protector.IsHardwareBacked);
        }
    }
}
