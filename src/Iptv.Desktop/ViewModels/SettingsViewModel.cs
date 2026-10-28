using System;
using System.Runtime.InteropServices;
using Iptv.Core.Security;
using Iptv.Desktop.Security;

namespace Iptv.Desktop.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ILibraryStateProtector? _protector;
    private string _selectedTheme = "深色";
    private string _defaultSection = "频道";
    private string _globalDefaultEpgUrl = string.Empty;

    public string[] AvailableThemes { get; } = { "跟随系统", "深色", "浅色" };
    public string[] AvailableDefaultSections { get; } = { "频道", "收藏", "配置" };

    public string GlobalDefaultEpgUrl
    {
        get => _globalDefaultEpgUrl;
        set => SetProperty(ref _globalDefaultEpgUrl, value);
    }

    public string SelectedTheme
    {
        get => _selectedTheme;
        set => SetProperty(ref _selectedTheme, value);
    }

    public string DefaultSection
    {
        get => _defaultSection;
        set => SetProperty(ref _defaultSection, value);
    }

    public string ProtectionStatus => _protector switch
    {
        WindowsDpapiStateProtector dpapi => dpapi.ProtectionDescription,
        SessionMemoryStateProtector mem => mem.ProtectionDescription,
        _ => (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Windows DPAPI (CurrentUser) 加密保护"
            : "Mac 开发环境：会话内存保护")
    };

    public bool IsHardwareProtected => _protector switch
    {
        WindowsDpapiStateProtector dpapi => dpapi.IsHardwareBacked,
        SessionMemoryStateProtector mem => mem.IsHardwareBacked,
        _ => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
    };

    public SettingsViewModel(ILibraryStateProtector? protector = null)
    {
        _protector = protector;
    }
}
