using System;
using System.IO;
using Iptv.Core.Models;

namespace Iptv.Desktop.Services;

/// <summary>Transient local-file input only; never creates a persisted source.</summary>
public static class LocalMediaService
{
    public const string SourceMarker = "local-media";
    public static string[] MediaPatterns { get; } = { "*.mp4", "*.mkv", "*.ts", "*.mov", "*.mp3", "*.wav", "*.m4a", "*.aac", "*.flac", "*.ogg", "*.avi", "*.webm" };

    public static Channel CreateChannel(string input)
    {
        var uri = ValidateFile(input);
        return new Channel(Guid.NewGuid().ToString("N"), SourceMarker, Path.GetFileName(uri.LocalPath), uri.AbsoluteUri);
    }

    public static Uri ValidateFile(string input)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(input) || input.StartsWith("\\\\", StringComparison.Ordinal) || input.StartsWith("//", StringComparison.Ordinal)
                || input.Contains('\0')) throw new IOException();
            Uri uri;
            if (input.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(input, UriKind.Absolute, out var parsed)) throw new IOException();
                uri = parsed;
            }
            else
            {
                if (!Path.IsPathFullyQualified(input)) throw new IOException();
                uri = new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = Path.GetFullPath(input).Replace("%", "%25", StringComparison.Ordinal) }.Uri;
            }
            if (!uri.IsFile || uri.IsUnc || !string.IsNullOrEmpty(uri.Host)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo)) throw new IOException();
            var path = uri.LocalPath;
            if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) throw new IOException();
            if (OperatingSystem.IsWindows() && (path[2..].Contains(':') || new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)) throw new IOException();
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 || file.Length == 0
                || !Array.Exists(MediaPatterns, pattern => Path.GetExtension(path).Equals(pattern[1..], StringComparison.OrdinalIgnoreCase))) throw new IOException();
            return new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = Path.GetFullPath(path).Replace("%", "%25", StringComparison.Ordinal) }.Uri;
        }
        catch (Exception) { throw new InvalidOperationException("请选择存在且非空的本地音视频文件；网络地址、共享路径和链接文件不受支持。"); }
    }

    public static bool IsFileCandidate(string input)
        => input.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || input.StartsWith("\\\\", StringComparison.Ordinal)
            || input.StartsWith("//", StringComparison.Ordinal) || Path.IsPathFullyQualified(input);
}
