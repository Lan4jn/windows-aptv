using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Library;
using Iptv.Core.Models;
using Iptv.Core.Security;
using Xunit;

namespace Iptv.Core.Tests.Library;

public class LibraryStateStoreTests
{
    private class FakeTestProtector : ILibraryStateProtector
    {
        private const byte XorKey = 0x5A;

        public byte[] Protect(byte[] plaintext)
        {
            var header = Encoding.UTF8.GetBytes("PROT:");
            var cipher = plaintext.Select(b => (byte)(b ^ XorKey)).ToArray();
            var result = new byte[header.Length + cipher.Length];
            Buffer.BlockCopy(header, 0, result, 0, header.Length);
            Buffer.BlockCopy(cipher, 0, result, header.Length, cipher.Length);
            return result;
        }

        public byte[] Unprotect(byte[] ciphertext)
        {
            var header = Encoding.UTF8.GetBytes("PROT:");
            if (ciphertext.Length < header.Length)
            {
                throw new InvalidOperationException("Ciphertext too short");
            }

            var cipher = new byte[ciphertext.Length - header.Length];
            Buffer.BlockCopy(ciphertext, header.Length, cipher, 0, cipher.Length);
            return cipher.Select(b => (byte)(b ^ XorKey)).ToArray();
        }
    }

    [Fact]
    public async Task SaveAndLoad_NonSensitiveState_RoundtripsSuccessfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lib_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new LibraryStateStore(tempDir);
            var channel = new Channel("ch-1", "src-1", "CCTV 1", "http://example.com/cctv1.m3u8");
            var source = new SourceConfig("src-1", "Public M3U", SourceKind.M3u, "http://example.com/list.m3u");

            var state = new LibraryState
            {
                Sources = new[] { source },
                Favorites = new[] { channel },
                RecentHistory = new[] { new RecentlyPlayedItem(channel, DateTimeOffset.UtcNow) }
            };

            await store.SaveAsync(state);
            var loaded = await store.LoadAsync();

            Assert.Single(loaded.Sources);
            Assert.Equal("Public M3U", loaded.Sources[0].Name);
            Assert.Single(loaded.Favorites);
            Assert.Equal("CCTV 1", loaded.Favorites[0].Name);
            Assert.Single(loaded.RecentHistory);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Save_SensitiveDataWithoutProtector_ThrowsInvalidOperationException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lib_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new LibraryStateStore(tempDir, protector: null);

            var sensitiveHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer secret-token-123" };
            var sensitiveChannel = new Channel("ch-priv", "src-priv", "VIP Channel", "http://example.com/live.m3u8", headers: sensitiveHeaders);

            var state = new LibraryState
            {
                Favorites = new[] { sensitiveChannel }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(state));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Save_XtreamSourceWithNullPasswordWithoutProtector_ThrowsInvalidOperationException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lib_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new LibraryStateStore(tempDir, protector: null);

            // Password is null (e.g. after deserialization where Password was JsonIgnored)
            var xtreamSource = new SourceConfig("xt-1", "My Xtream", SourceKind.Xtream, "http://xtream.server.com:8080", password: null);

            var state = new LibraryState
            {
                Sources = new[] { xtreamSource }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(state));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Save_XtreamHistoryUrlWithoutProtector_ThrowsInvalidOperationException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lib_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new LibraryStateStore(tempDir, protector: null);

            // Xtream channel URL structure contains credentials in path: /live/user/pass/id.ts
            var xtreamChannel = new Channel("ch-xt", "src-1", "Xtream Channel", "http://xtream.server.com:8080/live/john_doe/superSecretPass/1024.ts");

            var state = new LibraryState
            {
                RecentHistory = new[] { new RecentlyPlayedItem(xtreamChannel, DateTimeOffset.UtcNow) }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(state));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Save_CustomEpgUrlWithTokenWithoutProtector_ThrowsInvalidOperationException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lib_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new LibraryStateStore(tempDir, protector: null);
            var source = new SourceConfig("src-1", "M3U", SourceKind.M3u, "http://server.com/list.m3u", customEpgUrl: "http://epg.com/xmltv.php?token=myToken123");

            var state = new LibraryState
            {
                Sources = new[] { source }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(state));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Save_CookieHeaderWithoutProtector_ThrowsInvalidOperationException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lib_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new LibraryStateStore(tempDir, protector: null);
            var headers = new Dictionary<string, string> { ["Cookie"] = "sessionid=xyz123" };
            var source = new SourceConfig("src-1", "M3U", SourceKind.M3u, "http://server.com/list.m3u", headers: headers);

            var state = new LibraryState
            {
                Sources = new[] { source }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(state));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Save_SensitiveDataWithProtector_EncryptsPayloadAndPlaintextNotFoundInFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lib_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var protector = new FakeTestProtector();
            var store = new LibraryStateStore(tempDir, protector: protector);

            var secretUrl = "http://server.com/live/user/secretPass123/1.ts";
            var sensitiveChannel = new Channel("ch-priv", "src-priv", "VIP Channel", secretUrl);

            var state = new LibraryState
            {
                Favorites = new[] { sensitiveChannel }
            };

            await store.SaveAsync(state);

            // Verify raw file content on disk does NOT contain the secret token in plain text
            var rawFile = store.StateFilePath;
            var rawBytes = await File.ReadAllBytesAsync(rawFile);
            var rawString = Encoding.UTF8.GetString(rawBytes);

            Assert.DoesNotContain("secretPass123", rawString);

            // Load back with protector and verify state recovers
            var loaded = await store.LoadAsync();
            Assert.Single(loaded.Favorites);
            Assert.Equal(secretUrl, loaded.Favorites[0].StreamUrl);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Save_Cancelled_CleansUpTemporaryFileWithoutLeavingResidue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lib_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new LibraryStateStore(tempDir);
            var state = new LibraryState
            {
                Sources = new[] { new SourceConfig("src-1", "M3U", SourceKind.M3u, "http://server.com/list.m3u") }
            };

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Pre-cancel

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(state, cts.Token));

            // Verify no temp files remain
            var tempFiles = Directory.GetFiles(tempDir, "*.tmp");
            Assert.Empty(tempFiles);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Load_CorruptedFile_CreatesBackupWithoutDestroyingOriginal()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lib_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new LibraryStateStore(tempDir);
            var filePath = store.StateFilePath;

            // Write corrupted JSON
            await File.WriteAllTextAsync(filePath, "{ this is not valid json content !!! ");

            await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAsync());

            // Check that a corrupted backup file was created in tempDir
            var backupFiles = Directory.GetFiles(tempDir, "*corrupt*");
            Assert.NotEmpty(backupFiles);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Load_Cancelled_DoesNotCreateCorruptBackup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lib_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new LibraryStateStore(tempDir);
            var filePath = store.StateFilePath;
            await File.WriteAllTextAsync(filePath, "{}");

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cts.Token));

            // Must NOT have created corrupt backup on cancellation
            var backupFiles = Directory.GetFiles(tempDir, "*corrupt*");
            Assert.Empty(backupFiles);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Load_MissingProtectorOnProtectedFile_ThrowsInvalidOperationExceptionWithoutCorruptBackup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lib_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var protector = new FakeTestProtector();
            var savingStore = new LibraryStateStore(tempDir, protector: protector);
            var state = new LibraryState
            {
                Favorites = new[] { new Channel("ch-1", "src-1", "Ch", "http://server.com/live/u/p/1.ts") }
            };
            await savingStore.SaveAsync(state);

            // Now load with store that has NO protector
            var plainStore = new LibraryStateStore(tempDir, protector: null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => plainStore.LoadAsync());

            // Should NOT treat missing protector as corrupted backup
            var backupFiles = Directory.GetFiles(tempDir, "*corrupt*");
            Assert.Empty(backupFiles);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ProtectedSourcePassword_RoundtripsWithoutOrdinaryJsonDisclosure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lib_test_{Guid.NewGuid():N}");
        var store = new LibraryStateStore(tempDir, new FakeTestProtector());
        var source = new SourceConfig("s", "synthetic", SourceKind.Xtream, "https://example.test", username: "fixture-user", password: "fixture-password-S1");
        var state = new LibraryState { Sources = new[] { source } };
        try
        {
            Assert.DoesNotContain("fixture-password-S1", JsonSerializer.Serialize(state));
            await store.SaveAsync(state);
            Assert.DoesNotContain("fixture-password-S1", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(store.StateFilePath)));
            var loaded = await store.LoadAsync();
            Assert.Equal("fixture-password-S1", loaded.Sources[0].Password);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Favorites_Deduplicate_RetainsLatestItem()
    {
        var ch1 = new Channel("ch-1", "src-1", "Name Old", "http://example.com/1.m3u8");
        var ch2 = new Channel("ch-1", "src-1", "Name New", "http://example.com/1.m3u8");

        var state = new LibraryState
        {
            Favorites = new[] { ch1 }
        };

        var updated = state.WithFavorite(ch2);

        Assert.Single(updated.Favorites);
        Assert.Equal("Name New", updated.Favorites[0].Name);
    }

    [Fact]
    public void RecentHistory_BoundedToLimit_CapsAtConfiguredMax()
    {
        var state = new LibraryState();
        var limit = 5;

        for (int i = 0; i < 10; i++)
        {
            var ch = new Channel($"ch-{i}", "src-1", $"Channel {i}", $"http://example.com/{i}.m3u8");
            state = state.WithRecentPlayed(ch, DateTimeOffset.UtcNow.AddMinutes(i), maxLimit: limit);
        }

        Assert.Equal(limit, state.RecentHistory.Count);
        // Newest played item should be at the top
        Assert.Equal("Channel 9", state.RecentHistory[0].Channel.Name);
    }
}
