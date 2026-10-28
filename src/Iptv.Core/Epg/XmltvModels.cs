using System.Collections.Generic;
using Iptv.Core.Models;

namespace Iptv.Core.Epg;

/// <summary>
/// Represents a channel metadata definition inside an XMLTV feed.
/// </summary>
public sealed record XmltvChannel(string Id, string DisplayName);

/// <summary>
/// Result of parsing an XMLTV feed.
/// </summary>
public sealed record XmltvParseResult(
    IReadOnlyList<XmltvChannel> Channels,
    IReadOnlyList<EpgProgramme> Programmes,
    IReadOnlyList<string> Warnings);
