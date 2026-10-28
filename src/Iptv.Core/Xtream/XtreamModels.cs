using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iptv.Core.Xtream;

/// <summary>
/// Account information returned by Xtream Codes player_api.php.
/// </summary>
public sealed record XtreamAccountInfo
{
    public string Username { get; init; }
    public bool IsAuthenticated { get; init; }
    public string Status { get; init; }
    public DateTimeOffset? ExpirationDate { get; init; }
    public int MaxConnections { get; init; }

    public XtreamAccountInfo(
        string username,
        bool isAuthenticated,
        string status,
        DateTimeOffset? expirationDate = null,
        int maxConnections = 1)
    {
        Username = username;
        IsAuthenticated = isAuthenticated;
        Status = status;
        ExpirationDate = expirationDate;
        MaxConnections = maxConnections;
    }
}

/// <summary>
/// Live stream category returned by get_live_categories.
/// </summary>
public sealed record XtreamCategory
{
    [JsonPropertyName("category_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string CategoryId { get; init; } = "";

    [JsonPropertyName("category_name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string CategoryName { get; init; } = "";

    [JsonPropertyName("parent_id")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int ParentId { get; init; }
}

/// <summary>
/// Live stream entry returned by get_live_streams.
/// </summary>
public sealed record XtreamLiveStream
{
    [JsonPropertyName("num")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? Num { get; init; }

    [JsonPropertyName("name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Name { get; init; }

    [JsonPropertyName("stream_type")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? StreamType { get; init; }

    [JsonPropertyName("stream_id")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int StreamId { get; init; }

    [JsonPropertyName("stream_icon")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? StreamIcon { get; init; }

    [JsonPropertyName("epg_channel_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? EpgChannelId { get; init; }

    [JsonPropertyName("category_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? CategoryId { get; init; }
}

public sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out long l) ? l.ToString() : reader.GetDouble().ToString(),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value == null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

public sealed class FlexibleIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return reader.GetInt32();
        if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out int val)) return val;
        return 0;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

public sealed class FlexibleNullableIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return reader.GetInt32();
        if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out int val)) return val;
        if (reader.TokenType == JsonTokenType.Null) return null;
        return null;
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}
