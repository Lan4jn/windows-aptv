using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Models;

namespace Iptv.Core.Xtream;

public sealed class XtreamClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly IReadOnlyDictionary<string, string>? _customHeaders;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public XtreamClient(
        HttpClient httpClient,
        string baseUrl,
        string username,
        string password,
        IReadOnlyDictionary<string, string>? customHeaders = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("Base URL cannot be empty", nameof(baseUrl));
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Username cannot be empty", nameof(username));

        // Validate BaseUrl strictly: absolute, http/https, no userinfo, no query, no fragment
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Base URL '{baseUrl}' must be a valid absolute URI.", nameof(baseUrl));
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"Base URL scheme must be HTTP or HTTPS, found '{uri.Scheme}'.", nameof(baseUrl));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Base URL must not contain user credentials.", nameof(baseUrl));
        }

        if (!string.IsNullOrEmpty(uri.Query))
        {
            throw new ArgumentException("Base URL must not contain query parameters.", nameof(baseUrl));
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Base URL must not contain URI fragment.", nameof(baseUrl));
        }

        // Preserve legitimate subpath and trim trailing slash
        _baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        _username = username.Trim();
        _password = password ?? "";
        _customHeaders = customHeaders != null ? new Dictionary<string, string>(customHeaders) : null;
    }

    /// <summary>
    /// Authenticates with the Xtream server and returns the account state.
    /// </summary>
    public async Task<XtreamAccountInfo> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        var url = BuildPlayerApiUrl();
        var json = await SendSafeGetRequestAsync(url, cancellationToken).ConfigureAwait(false);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("user_info", out var userInfo))
            {
                throw new XtreamAuthException("Invalid response from server: 'user_info' element is missing.");
            }

            int auth = 0;
            if (userInfo.TryGetProperty("auth", out var authElem))
            {
                if (authElem.ValueKind == JsonValueKind.Number) auth = authElem.GetInt32();
                else if (authElem.ValueKind == JsonValueKind.String && int.TryParse(authElem.GetString(), out int parsedAuth)) auth = parsedAuth;
            }

            string status = userInfo.TryGetProperty("status", out var statusElem) ? statusElem.GetString() ?? "Unknown" : "Unknown";

            if (auth == 0)
            {
                throw new XtreamAuthException("Authentication failed: invalid username or password.");
            }

            if (status.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Banned", StringComparison.OrdinalIgnoreCase))
            {
                throw new XtreamAuthException($"Account is {status}.");
            }

            if (status.Equals("Expired", StringComparison.OrdinalIgnoreCase))
            {
                throw new XtreamAuthException("Account is Expired.");
            }

            DateTimeOffset? expDate = null;
            if (userInfo.TryGetProperty("exp_date", out var expElem))
            {
                if (expElem.ValueKind == JsonValueKind.Number && expElem.TryGetInt64(out long expSec) && expSec > 0)
                {
                    expDate = DateTimeOffset.FromUnixTimeSeconds(expSec);
                }
                else if (expElem.ValueKind == JsonValueKind.String && long.TryParse(expElem.GetString(), out long expStrSec) && expStrSec > 0)
                {
                    expDate = DateTimeOffset.FromUnixTimeSeconds(expStrSec);
                }
            }

            if (expDate.HasValue && expDate.Value < DateTimeOffset.UtcNow)
            {
                throw new XtreamAuthException($"Account has expired on {expDate.Value:yyyy-MM-dd HH:mm:ss} UTC.");
            }

            int maxConnections = 1;
            if (userInfo.TryGetProperty("max_connections", out var maxElem))
            {
                if (maxElem.ValueKind == JsonValueKind.Number) maxConnections = maxElem.GetInt32();
                else if (maxElem.ValueKind == JsonValueKind.String && int.TryParse(maxElem.GetString(), out int parsedMax)) maxConnections = parsedMax;
            }

            string username = userInfo.TryGetProperty("username", out var uElem) ? uElem.GetString() ?? _username : _username;

            return new XtreamAccountInfo(username, true, status, expDate, maxConnections);
        }
        catch (JsonException ex)
        {
            throw new XtreamException("Failed to parse authentication response JSON: " + MaskSensitiveText(ex.Message));
        }
    }

    /// <summary>
    /// Fetches all live stream categories.
    /// </summary>
    public async Task<IReadOnlyList<XtreamCategory>> GetLiveCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var url = BuildPlayerApiUrl("get_live_categories");
        var json = await SendSafeGetRequestAsync(url, cancellationToken).ConfigureAwait(false);

        try
        {
            var list = JsonSerializer.Deserialize<List<XtreamCategory>>(json, JsonOptions);
            return list ?? new List<XtreamCategory>();
        }
        catch (JsonException ex)
        {
            throw new XtreamException("Failed to parse live categories JSON: " + MaskSensitiveText(ex.Message));
        }
    }

    /// <summary>
    /// Fetches live streams, optionally filtered by category ID.
    /// </summary>
    public async Task<IReadOnlyList<XtreamLiveStream>> GetLiveStreamsAsync(string? categoryId = null, CancellationToken cancellationToken = default)
    {
        var action = "get_live_streams";
        var url = !string.IsNullOrWhiteSpace(categoryId)
            ? BuildPlayerApiUrl(action, ("category_id", categoryId))
            : BuildPlayerApiUrl(action);

        var json = await SendSafeGetRequestAsync(url, cancellationToken).ConfigureAwait(false);

        try
        {
            var list = JsonSerializer.Deserialize<List<XtreamLiveStream>>(json, JsonOptions);
            return list ?? new List<XtreamLiveStream>();
        }
        catch (JsonException ex)
        {
            throw new XtreamException("Failed to parse live streams JSON: " + MaskSensitiveText(ex.Message));
        }
    }

    /// <summary>
    /// High-level method: fetches live categories and streams, then maps them into normalized Channel models.
    /// </summary>
    public async Task<IReadOnlyList<Channel>> GetLiveChannelsAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("SourceId cannot be empty", nameof(sourceId));

        var categories = await GetLiveCategoriesAsync(cancellationToken).ConfigureAwait(false);
        // Robust against duplicate category IDs: use indexer assignment
        var categoryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in categories)
        {
            if (!string.IsNullOrWhiteSpace(c.CategoryId) && !categoryMap.ContainsKey(c.CategoryId))
            {
                categoryMap[c.CategoryId] = c.CategoryName;
            }
        }

        var streams = await GetLiveStreamsAsync(null, cancellationToken).ConfigureAwait(false);
        var channels = new List<Channel>(streams.Count);

        foreach (var stream in streams)
        {
            string channelId = $"{sourceId}:xtream:{stream.StreamId}";
            string name = !string.IsNullOrWhiteSpace(stream.Name) ? stream.Name : $"Channel {stream.StreamId}";

            string? group = null;
            if (!string.IsNullOrWhiteSpace(stream.CategoryId) && categoryMap.TryGetValue(stream.CategoryId, out var groupName))
            {
                group = groupName;
            }

            // Stream URL format: {baseUrl}/live/{username}/{password}/{stream_id}.ts
            string streamUrl = $"{_baseUrl}/live/{Uri.EscapeDataString(_username)}/{Uri.EscapeDataString(_password)}/{stream.StreamId}.ts";

            var channel = new Channel(
                id: channelId,
                sourceId: sourceId,
                name: name,
                streamUrl: streamUrl,
                group: group,
                tvgId: !string.IsNullOrWhiteSpace(stream.EpgChannelId) ? stream.EpgChannelId : null,
                tvgName: name,
                logoUrl: !string.IsNullOrWhiteSpace(stream.StreamIcon) ? stream.StreamIcon : null,
                headers: _customHeaders
            );

            channels.Add(channel);
        }

        return channels;
    }

    /// <summary>
    /// Gets the full XMLTV endpoint URL provided by this Xtream server.
    /// </summary>
    public string GetXmltvUrl()
    {
        return $"{_baseUrl}/xmltv.php?username={Uri.EscapeDataString(_username)}&password={Uri.EscapeDataString(_password)}";
    }

    /// <summary>
    /// Gets the masked XMLTV endpoint URL safe for logs and user interfaces.
    /// </summary>
    public string GetMaskedXmltvUrl()
    {
        return $"{_baseUrl}/xmltv.php?username={Uri.EscapeDataString(_username)}&password=***";
    }

    private string BuildPlayerApiUrl(string? action = null, params (string Key, string Value)[] extraParams)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(_baseUrl);
        sb.Append("/player_api.php?username=");
        sb.Append(Uri.EscapeDataString(_username));
        sb.Append("&password=");
        sb.Append(Uri.EscapeDataString(_password));

        if (!string.IsNullOrWhiteSpace(action))
        {
            sb.Append("&action=");
            sb.Append(Uri.EscapeDataString(action));
        }

        foreach (var (k, v) in extraParams)
        {
            sb.Append('&');
            sb.Append(Uri.EscapeDataString(k));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(v));
        }

        return sb.ToString();
    }

    private async Task<string> SendSafeGetRequestAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_customHeaders != null)
        {
            foreach (var (k, v) in _customHeaders)
            {
                request.Headers.TryAddWithoutValidation(k, v);
            }
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string safeReason = MaskSensitiveText(response.ReasonPhrase ?? "");
                throw new XtreamException($"Xtream server returned HTTP {(int)response.StatusCode} {safeReason}".Trim());
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Propagate caller's own cancellation token directly
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Internal HttpClient timeout (caller token was not cancelled)
            string safeMessage = MaskSensitiveText(ex.Message);
            throw new XtreamException($"Network error: Xtream request timed out ({safeMessage})");
        }
        catch (HttpRequestException ex)
        {
            // Do NOT attach the raw HttpRequestException as InnerException, because ex.ToString() or ex.Message
            // might contain the request URL with unmasked passwords.
            string safeMessage = MaskSensitiveText(ex.Message);
            throw new XtreamException($"Network error when contacting Xtream server: {safeMessage}");
        }
    }

    private string MaskSensitiveText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (string.IsNullOrEmpty(_password)) return text;

        var result = text.Replace(_password, "***");
        var encodedPw = Uri.EscapeDataString(_password);
        if (!string.Equals(_password, encodedPw, StringComparison.Ordinal))
        {
            result = result.Replace(encodedPw, "***");
        }
        return result;
    }
}
