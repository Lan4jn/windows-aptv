using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Iptv.Core.Models;

namespace Iptv.Core.Epg;

public sealed class XmltvParser
{
    private static readonly XmlReaderSettings SafeReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        Async = true,
        IgnoreWhitespace = true,
        IgnoreComments = true
    };

    public const long MaxStreamBufferSize = 100 * 1024 * 1024; // 100 MB safety limit against decompression bombs

    public long MaxBufferBytes { get; }

    public XmltvParser(long maxBufferBytes = MaxStreamBufferSize)
    {
        if (maxBufferBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferBytes));
        MaxBufferBytes = maxBufferBytes;
    }

    public Task<XmltvParseResult> ParseAsync(string xmlContent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
            throw new ArgumentException("XML content cannot be empty", nameof(xmlContent));

        if (Encoding.UTF8.GetByteCount(xmlContent) > MaxBufferBytes)
            throw new InvalidOperationException("EPG content exceeds the safety limit.");
        var bytes = Encoding.UTF8.GetBytes(xmlContent);
        return ParseAsync(new MemoryStream(bytes), cancellationToken);
    }

    public async Task<XmltvParseResult> ParseGzipAsync(Stream gzipStream, CancellationToken cancellationToken = default)
    {
        using var decompressed = new MemoryStream();
        await using (var gz = new GZipStream(gzipStream, CompressionMode.Decompress, leaveOpen: true))
        {
            await CopyBoundedAsync(gz, decompressed, MaxBufferBytes, cancellationToken).ConfigureAwait(false);
        }
        decompressed.Position = 0;
        return await ParseAsync(decompressed, cancellationToken).ConfigureAwait(false);
    }

    public async Task<XmltvParseResult> ParseAutoAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (!stream.CanSeek)
        {
            using var ms = new MemoryStream();
            await CopyBoundedAsync(stream, ms, MaxBufferBytes, cancellationToken).ConfigureAwait(false);
            ms.Position = 0;
            return await ParseAutoAsync(ms, cancellationToken).ConfigureAwait(false);
        }

        long origPos = stream.Position;
        byte[] header = new byte[2];
        int read = await stream.ReadAsync(header.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
        stream.Position = origPos;

        if (read == 2 && header[0] == 0x1F && header[1] == 0x8B)
        {
            return await ParseGzipAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        return await ParseAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public async Task<XmltvParseResult> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var channels = new List<XmltvChannel>();
        var programmes = new List<EpgProgramme>();
        var warnings = new List<string>();

        cancellationToken.ThrowIfCancellationRequested();
        var settings = SafeReaderSettings.Clone();
        settings.MaxCharactersInDocument = MaxBufferBytes;
        using var reader = XmlReader.Create(stream, settings);

        try
        {
            await reader.MoveToContentAsync().ConfigureAwait(false);
        }
        catch (XmlException ex)
        {
            throw new FormatException($"Malformed XML content: {ex.Message}", ex);
        }

        // Strict root node validation: must be <tv> at depth 0
        if (reader.NodeType != XmlNodeType.Element || reader.Depth != 0 || !reader.LocalName.Equals("tv", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Invalid XMLTV feed: root element must be <tv> at depth 0, found <{reader.Name}> at depth {reader.Depth}.");
        }

        if (!reader.IsEmptyElement)
        {
            try
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1)
                    {
                        if (reader.Name.Equals("channel", StringComparison.OrdinalIgnoreCase))
                        {
                            using var subReader = reader.ReadSubtree();
                            var xElem = await XElement.LoadAsync(subReader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
                            var ch = ParseChannelElement(xElem, warnings);
                            if (ch != null) channels.Add(ch);
                        }
                        else if (reader.Name.Equals("programme", StringComparison.OrdinalIgnoreCase))
                        {
                            using var subReader = reader.ReadSubtree();
                            var xElem = await XElement.LoadAsync(subReader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
                            var prog = ParseProgrammeElement(xElem, warnings);
                            if (prog != null) programmes.Add(prog);
                        }
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0 && reader.Name.Equals("tv", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
            catch (XmlException ex)
            {
                throw new FormatException($"Malformed XMLTV content: {ex.Message}", ex);
            }
        }

        return new XmltvParseResult(channels, programmes, warnings);
    }

    internal static async Task CopyBoundedAsync(Stream source, Stream destination, long maxBytes, CancellationToken ct)
    {
        byte[] buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            totalRead += read;
            if (totalRead > maxBytes)
            {
                throw new InvalidOperationException($"Stream exceeded maximum allowed safety buffer size of {maxBytes} bytes.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private static XmltvChannel? ParseChannelElement(XElement element, List<string> warnings)
    {
        string? id = element.Attribute("id")?.Value;
        string? displayName = element.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("display-name", StringComparison.OrdinalIgnoreCase))?.Value;

        if (string.IsNullOrWhiteSpace(id))
        {
            warnings.Add("Skipped <channel> element missing 'id' attribute.");
            return null;
        }

        return new XmltvChannel(id, displayName ?? id);
    }

    private static EpgProgramme? ParseProgrammeElement(XElement element, List<string> warnings)
    {
        string? channelId = element.Attribute("channel")?.Value;
        string? startStr = element.Attribute("start")?.Value;
        string? stopStr = element.Attribute("stop")?.Value;

        string? title = element.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase))?.Value;
        string? desc = element.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("desc", StringComparison.OrdinalIgnoreCase))?.Value;

        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(startStr) || string.IsNullOrWhiteSpace(stopStr))
        {
            warnings.Add($"Skipped programme on channel '{channelId}': missing required attributes (start/stop/channel).");
            return null;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "No Title";
        }

        DateTimeOffset start;
        DateTimeOffset stop;

        try
        {
            start = ParseXmltvDateTime(startStr, warnings);
            stop = ParseXmltvDateTime(stopStr, warnings);
        }
        catch (Exception ex)
        {
            warnings.Add($"Skipped programme '{title}' on channel '{channelId}': invalid timestamp ({ex.Message}).");
            return null;
        }

        if (stop <= start)
        {
            warnings.Add($"Skipped programme '{title}' on channel '{channelId}': End time ({stop}) <= Start time ({start}).");
            return null;
        }

        return new EpgProgramme(channelId, title, start, stop, desc);
    }

    public static DateTimeOffset ParseXmltvDateTime(string raw, List<string> warnings)
    {
        var trimmed = raw.Trim();

        // Standard XMLTV format: yyyyMMddHHmmss [+/-]HHmm
        if (trimmed.Length >= 15 && trimmed[14] == ' ')
        {
            var datePart = trimmed[..14];
            var offsetPart = trimmed[15..].Trim();

            if (DateTime.TryParseExact(datePart, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                if (offsetPart.Equals("Z", StringComparison.OrdinalIgnoreCase))
                {
                    return new DateTimeOffset(dt, TimeSpan.Zero);
                }

                if (TryParseOffset(offsetPart, out var offset))
                {
                    return new DateTimeOffset(dt, offset);
                }
            }
        }

        // Exact without offset (yyyyMMddHHmmss) -> assume UTC with warning
        if (trimmed.Length == 14 && DateTime.TryParseExact(trimmed, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var utcDt))
        {
            warnings.Add($"Timestamp '{trimmed}' has no timezone offset; defaulted to UTC.");
            return new DateTimeOffset(DateTime.SpecifyKind(utcDt, DateTimeKind.Utc), TimeSpan.Zero);
        }

        // Fallback standard DateTimeOffset.Parse
        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            return dto;
        }

        throw new FormatException($"Cannot parse XMLTV timestamp '{raw}'");
    }

    private static bool TryParseOffset(string offsetPart, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(offsetPart)) return false;

        offsetPart = offsetPart.Trim();
        if (offsetPart.Equals("Z", StringComparison.OrdinalIgnoreCase))
        {
            offset = TimeSpan.Zero;
            return true;
        }

        char sign = offsetPart[0];
        if (sign != '+' && sign != '-') return false;

        string digits = offsetPart[1..].Replace(":", "");
        if (digits.Length == 4 && int.TryParse(digits[..2], out int hours) && int.TryParse(digits[2..], out int minutes))
        {
            offset = new TimeSpan(hours, minutes, 0);
            if (sign == '-') offset = -offset;
            return true;
        }

        return false;
    }
}
