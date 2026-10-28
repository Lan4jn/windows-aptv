using System;

namespace Iptv.Core.Playlists;

/// <summary>
/// Exception thrown when an M3U playlist format is invalid or cannot be parsed.
/// </summary>
public class M3uParseException : Exception
{
    public M3uParseException(string message) : base(message) { }
    public M3uParseException(string message, Exception innerException) : base(message, innerException) { }
}
