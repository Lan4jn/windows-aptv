using System;
using Iptv.Core.Models;

namespace Iptv.Desktop.ViewModels;

public sealed class SourceConfigItemViewModel : ViewModelBase
{
    public SourceConfig Config { get; }
    public string Id => Config.Id;
    public string Name => Config.Name;
    public SourceKind Kind => Config.Kind;
    public string Location => Config.Location;
    public string SafeDisplayLocation { get; }

    public SourceConfigItemViewModel(SourceConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        SafeDisplayLocation = SanitizeUrl(config.Location);
    }

    public static string SanitizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) || uri.HostNameType == UriHostNameType.Unknown)
            return "地址已隐藏";
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}";
    }
}
