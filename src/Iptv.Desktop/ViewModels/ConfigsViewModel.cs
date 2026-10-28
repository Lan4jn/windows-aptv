using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Models;
using Iptv.Desktop.Services;

namespace Iptv.Desktop.ViewModels;

public sealed class AddConfigFormModel : ViewModelBase
{
    private SourceKind _kind = SourceKind.M3u;
    private string _name = "Default";
    private string _locationUrl = string.Empty;
    private string _serverUrl = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string? _customEpgUrl;
    private string? _userAgent;
    private string? _referer;
    private string? _validationErrorMessage;

    // 6 toggles matching REA baseline observations (default off)
    private bool _autoRefresh;
    private bool _closePreview;
    private bool _autoRefreshPreview;
    private bool _closeDelayDetection;
    private bool _closeBuiltinLogo;
    private bool _hideGroups;

    public SourceKind Kind
    {
        get => _kind;
        set => SetProperty(ref _kind, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string LocationUrl
    {
        get => _locationUrl;
        set => SetProperty(ref _locationUrl, value);
    }

    public string ServerUrl
    {
        get => _serverUrl;
        set => SetProperty(ref _serverUrl, value);
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string? CustomEpgUrl
    {
        get => _customEpgUrl;
        set => SetProperty(ref _customEpgUrl, value);
    }

    public string? UserAgent
    {
        get => _userAgent;
        set => SetProperty(ref _userAgent, value);
    }

    public string? Referer
    {
        get => _referer;
        set => SetProperty(ref _referer, value);
    }

    public string? ValidationErrorMessage
    {
        get => _validationErrorMessage;
        set => SetProperty(ref _validationErrorMessage, value);
    }

    public bool AutoRefresh
    {
        get => _autoRefresh;
        set => SetProperty(ref _autoRefresh, value);
    }

    public bool ClosePreview
    {
        get => _closePreview;
        set => SetProperty(ref _closePreview, value);
    }

    public bool AutoRefreshPreview
    {
        get => _autoRefreshPreview;
        set => SetProperty(ref _autoRefreshPreview, value);
    }

    public bool CloseDelayDetection
    {
        get => _closeDelayDetection;
        set => SetProperty(ref _closeDelayDetection, value);
    }

    public bool CloseBuiltinLogo
    {
        get => _closeBuiltinLogo;
        set => SetProperty(ref _closeBuiltinLogo, value);
    }

    public bool HideGroups
    {
        get => _hideGroups;
        set => SetProperty(ref _hideGroups, value);
    }

    public void Reset(SourceKind kind)
    {
        Kind = kind;
        Name = "Default";
        LocationUrl = string.Empty;
        ServerUrl = string.Empty;
        Username = string.Empty;
        Password = string.Empty;
        CustomEpgUrl = null;
        UserAgent = null;
        Referer = null;
        ValidationErrorMessage = null;
        AutoRefresh = false;
        ClosePreview = false;
        AutoRefreshPreview = false;
        CloseDelayDetection = false;
        CloseBuiltinLogo = false;
        HideGroups = false;
    }
}

public sealed class ConfigEditField : ViewModelBase
{
    public static string[] Actions { get; } = { "不修改", "替换", "清除" };
    public string Label { get; }
    private string _action = "不修改";
    private string _value = string.Empty;
    public string Action { get => _action; set => SetProperty(ref _action, value); }
    public string Value { get => _value; set => SetProperty(ref _value, value); }
    public ConfigEditField(string label) => Label = label;
    public string? Apply(string? current) => Action switch
    {
        "不修改" => current,
        "清除" => null,
        "替换" when Value.Length > 0 => Value,
        _ => throw new ArgumentException("请填写替换内容，或选择不修改/清除")
    };
}

public sealed class ConfigEditForm : ViewModelBase
{
    public string Id { get; }
    public string SafeLocation { get; }
    private string _name;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public ConfigEditField Location { get; } = new("地址");
    public ConfigEditField Epg { get; } = new("EPG 地址");
    public ConfigEditField UserAgent { get; } = new("User-Agent");
    public ConfigEditField Referer { get; } = new("Referer");
    public ConfigEditField Username { get; } = new("用户名");
    public ConfigEditField Password { get; } = new("密码");
    public ConfigEditField[] Fields { get; }
    public ConfigEditForm(SourceConfig config)
    {
        Id = config.Id;
        _name = config.Name;
        SafeLocation = SourceConfigItemViewModel.SanitizeUrl(config.Location);
        Fields = new[] { Location, Epg, UserAgent, Referer, Username, Password };
    }
    public SourceConfig Apply(SourceConfig current)
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("请输入名称");
        var location = Location.Apply(current.Location);
        if (string.IsNullOrWhiteSpace(location)) throw new ArgumentException("地址不可清除");
        var headers = new System.Collections.Generic.Dictionary<string, string>(current.Headers ?? new System.Collections.Generic.Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        void SetHeader(string key, ConfigEditField field)
        {
            headers.TryGetValue(key, out var old);
            var value = field.Apply(old);
            if (value is null) headers.Remove(key); else headers[key] = value;
        }
        SetHeader("User-Agent", UserAgent);
        SetHeader("Referer", Referer);
        return current with { Name = Name.Trim(), Location = location, CustomEpgUrl = Epg.Apply(current.CustomEpgUrl),
            Username = Username.Apply(current.Username), Password = Password.Apply(current.Password), Headers = headers };
    }
}

public sealed class ConfigsViewModel : ViewModelBase
{
    private readonly ILibraryService _libraryService;
    private ConfigEditForm? _editForm;
    private bool _isSavingChanges;
    private string? _editError;
    public ConfigEditForm? EditForm { get => _editForm; private set { if (SetProperty(ref _editForm, value)) OnPropertyChanged(nameof(IsEditing)); } }
    public bool IsEditing => EditForm is not null;
    public bool IsSavingChanges { get => _isSavingChanges; private set => SetProperty(ref _isSavingChanges, value); }
    public string? EditError { get => _editError; private set => SetProperty(ref _editError, value); }
    public RelayCommand OpenEditCommand { get; }
    public RelayCommand CancelEditCommand { get; }
    public AsyncRelayCommand SaveEditCommand { get; }
    public AsyncRelayCommand MoveUpCommand { get; }
    public AsyncRelayCommand MoveDownCommand { get; }

    private bool _isAddDialogOpen;
    private SourceConfigItemViewModel? _selectedConfig;

    public ObservableCollection<SourceConfigItemViewModel> Configs { get; } = new();
    public ProxyProfilesViewModel ProxyProfilesVm { get; }
    public AddConfigFormModel AddForm { get; } = new();

    public bool IsAddDialogOpen
    {
        get => _isAddDialogOpen;
        set => SetProperty(ref _isAddDialogOpen, value);
    }

    public Func<SourceConfig?, Task>? SelectionChangedAsync { get; set; }
    public Func<SourceConfig, Task<bool>>? ConfirmDeleteAsync { get; set; }
    public Task SelectionLoadTask { get; private set; } = Task.CompletedTask;

    public SourceConfigItemViewModel? SelectedConfig
    {
        get => _selectedConfig;
        set
        {
            if (SetProperty(ref _selectedConfig, value))
                SelectionLoadTask = SelectionChangedAsync?.Invoke(value?.Config) ?? Task.CompletedTask;
        }
    }

    public RelayCommand OpenAddM3uDialogCommand { get; }
    public RelayCommand OpenAddXtreamDialogCommand { get; }
    public RelayCommand CloseAddDialogCommand { get; }
    public AsyncRelayCommand SaveAddFormCommand { get; }
    public AsyncRelayCommand DeleteConfigCommand { get; }

    public ConfigsViewModel(ILibraryService libraryService)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        ProxyProfilesVm = new ProxyProfilesViewModel(_libraryService, () => RefreshConfigsAsync());

        OpenEditCommand = new RelayCommand(parameter =>
        {
            if (!IsSavingChanges && parameter is SourceConfigItemViewModel item)
            { EditError = null; EditForm = new ConfigEditForm(item.Config); }
        });
        CancelEditCommand = new RelayCommand(() => { if (!IsSavingChanges) { EditForm = null; EditError = null; } });
        SaveEditCommand = new AsyncRelayCommand(async () =>
        {
            if (EditForm is null || IsSavingChanges) return;
            IsSavingChanges = true;
            EditError = null;
            try
            {
                var current = (await _libraryService.GetConfigsAsync()).Single(c => c.Id == EditForm.Id);
                var updated = EditForm.Apply(current);
                await _libraryService.SaveConfigAsync(updated);
                await RefreshConfigsAsync();
                EditForm = null;
            }
            catch (Exception) { EditError = "配置保存失败，请检查输入或存储后重试"; }
            finally { IsSavingChanges = false; }
        });
        MoveUpCommand = new AsyncRelayCommand(parameter => MoveSourceAsync(parameter, -1));
        MoveDownCommand = new AsyncRelayCommand(parameter => MoveSourceAsync(parameter, 1));

        OpenAddM3uDialogCommand = new RelayCommand(() =>
        {
            AddForm.Reset(SourceKind.M3u);
            IsAddDialogOpen = true;
        });

        OpenAddXtreamDialogCommand = new RelayCommand(() =>
        {
            AddForm.Reset(SourceKind.Xtream);
            IsAddDialogOpen = true;
        });

        CloseAddDialogCommand = new RelayCommand(() =>
        {
            IsAddDialogOpen = false;
        });

        SaveAddFormCommand = new AsyncRelayCommand(async () =>
        {
            var sourceId = Guid.NewGuid().ToString("N");
            var headers = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(AddForm.UserAgent)) headers["User-Agent"] = AddForm.UserAgent.Trim();
            if (!string.IsNullOrWhiteSpace(AddForm.Referer)) headers["Referer"] = AddForm.Referer.Trim();
            if (AddForm.Kind == SourceKind.M3u)
            {
                if (string.IsNullOrWhiteSpace(AddForm.LocationUrl))
                {
                    AddForm.ValidationErrorMessage = "请输入有效的 M3U 订阅链接或本地文件路径";
                    return;
                }

                var config = new SourceConfig(
                    id: sourceId,
                    name: string.IsNullOrWhiteSpace(AddForm.Name) ? "Default" : AddForm.Name,
                    kind: SourceKind.M3u,
                    location: AddForm.LocationUrl.Trim(),
                    customEpgUrl: string.IsNullOrWhiteSpace(AddForm.CustomEpgUrl) ? null : AddForm.CustomEpgUrl.Trim(),
                    headers: headers
                );

                await _libraryService.SaveConfigAsync(config);
            }
            else // Xtream
            {
                if (string.IsNullOrWhiteSpace(AddForm.ServerUrl))
                {
                    AddForm.ValidationErrorMessage = "请输入 Xtream 服务地址";
                    return;
                }

                if (string.IsNullOrWhiteSpace(AddForm.Username))
                {
                    AddForm.ValidationErrorMessage = "请输入用户名";
                    return;
                }

                if (string.IsNullOrWhiteSpace(AddForm.Password))
                {
                    AddForm.ValidationErrorMessage = "请输入密码";
                    return;
                }

                var config = new SourceConfig(
                    id: sourceId,
                    name: string.IsNullOrWhiteSpace(AddForm.Name) ? "Default" : AddForm.Name,
                    kind: SourceKind.Xtream,
                    location: AddForm.ServerUrl.Trim(),
                    customEpgUrl: string.IsNullOrWhiteSpace(AddForm.CustomEpgUrl) ? null : AddForm.CustomEpgUrl.Trim(),
                    username: AddForm.Username.Trim(),
                    password: AddForm.Password,
                    headers: headers
                );

                await _libraryService.SaveConfigAsync(config);
            }

            IsAddDialogOpen = false;
            await RefreshConfigsAsync();
            SelectedConfig = Configs.First(c => c.Id == sourceId);
            await SelectionLoadTask;
        });

        DeleteConfigCommand = new AsyncRelayCommand(async param =>
        {
            var config = param switch
            {
                SourceConfigItemViewModel item => item.Config,
                SourceConfig source => source,
                _ => null
            };
            if (config is null || ConfirmDeleteAsync is null || !await ConfirmDeleteAsync(config)) return;
            await _libraryService.DeleteConfigAsync(config.Id);
            await RefreshConfigsAsync();
        });
    }

    private async Task MoveSourceAsync(object? parameter, int delta)
    {
        if (IsSavingChanges || parameter is not SourceConfigItemViewModel item) return;
        var ids = Configs.Select(c => c.Id).ToList();
        var index = ids.IndexOf(item.Id);
        if (index < 0 || index + delta < 0 || index + delta >= ids.Count) return;
        (ids[index], ids[index + delta]) = (ids[index + delta], ids[index]);
        IsSavingChanges = true;
        EditError = null;
        try
        {
            await _libraryService.SaveSourceOrderAsync(ids);
            await RefreshConfigsAsync();
        }
        catch (Exception) { EditError = "排序保存失败，请检查存储后重试"; }
        finally { IsSavingChanges = false; }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await RefreshConfigsAsync(ct);
    }

    public async Task RefreshConfigsAsync(CancellationToken ct = default)
    {
        var list = await _libraryService.GetConfigsAsync(ct);
        var oldSelectedId = SelectedConfig?.Id;
        Configs.Clear();
        foreach (var cfg in list.OrderBy(config => config.Order))
        {
            Configs.Add(new SourceConfigItemViewModel(cfg));
        }
        SelectedConfig = Configs.FirstOrDefault(c => c.Id == oldSelectedId) ?? Configs.FirstOrDefault();
        await SelectionLoadTask;
        await ProxyProfilesVm.RefreshAsync(ct);
    }
}
