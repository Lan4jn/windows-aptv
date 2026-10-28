using System;

namespace Iptv.Core.Models;

/// <summary>
/// Represents an electronic programme guide (EPG) event.
/// </summary>
public sealed record EpgProgramme
{
    public string ChannelId { get; init; }
    public string Title { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public string? Description { get; init; }

    public EpgProgramme(
        string channelId,
        string title,
        DateTimeOffset start,
        DateTimeOffset end,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("ChannelId cannot be null or whitespace", nameof(channelId));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Title cannot be null or whitespace", nameof(title));
        if (end <= start) throw new ArgumentException("End time must be after Start time", nameof(end));

        ChannelId = channelId;
        Title = title;
        Start = start;
        End = end;
        Description = description;
    }
}
