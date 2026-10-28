using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Models;
using Iptv.Desktop.Services;

namespace Iptv.Desktop.ViewModels;

public sealed record ProxyChoice(string? Id, string Name);

public sealed class ProxyProfileItem
{
    public string Id { get; }
    public string Name { get; }
    public ProxyKind Kind { get; }
    public string SafeEndpoint { get; }
    public ProxyProfileItem(UserProxy proxy)
    {
        Id = proxy.Id; Name = proxy.Name; Kind = proxy.Kind;
        SafeEndpoint = ProxyProfilesViewModel.EndpointSummary(proxy.Endpoint);
    }
}

public sealed class ProxyProfileDraft : ViewModelBase
{
    public string? Id { get; }
    private string _name;
    private ProxyKind _kind;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public ProxyKind Kind { get => _kind; set => SetProperty(ref _kind, value); }
    public static ProxyKind[] Kinds { get; } = { ProxyKind.Http, ProxyKind.Socks5 };
    public string SafeEndpoint { get; }
    public ConfigEditField Endpoint { get; } = new("端点");
    public ConfigEditField Username { get; } = new("用户名");
    public ConfigEditField Password { get; } = new("密码");
    public ProxyProfileDraft(UserProxy? proxy = null)
    {
        Id = proxy?.Id; _name = proxy?.Name ?? string.Empty; _kind = proxy?.Kind ?? ProxyKind.Http;
        SafeEndpoint = proxy is null ? "新档案：请填写端点" : ProxyProfilesViewModel.EndpointSummary(proxy.Endpoint);
        if (proxy is null) Endpoint.Action = "替换";
    }
    public UserProxy Apply(UserProxy? current)
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException();
        var endpoint = Endpoint.Apply(current?.Endpoint);
        ProxyProfilesViewModel.ValidateEndpoint(endpoint, Kind);
        return new UserProxy(current?.Id ?? Guid.NewGuid().ToString("N"), Name.Trim(), Kind, endpoint!,
            Username.Apply(current?.Username), Password.Apply(current?.Password));
    }
}

public sealed class SourceProxyAssignment : ViewModelBase
{
    public string SourceId { get; }
    public string Name { get; }
    private string _currentProxyName;
    public string CurrentProxyName { get => _currentProxyName; private set => SetProperty(ref _currentProxyName, value); }
    private string? _savedId;
    public IReadOnlyList<ProxyChoice> Choices { get; }
    private ProxyChoice? _selected;
    public ProxyChoice? Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public SourceProxyAssignment(SourceConfig source, IReadOnlyList<ProxyChoice> choices)
    {
        SourceId = source.Id; Name = source.Name; _savedId = source.ProxyId; Choices = choices;
        _selected = choices.FirstOrDefault(choice => choice.Id == source.ProxyId);
        _currentProxyName = _selected?.Name ?? "档案缺失，请重新选择";
    }
    public void Cancel() => Selected = Choices.FirstOrDefault(choice => choice.Id == _savedId);
    public void MarkSaved(ProxyChoice choice)
    {
        _savedId = choice.Id;
        CurrentProxyName = choice.Name;
        Selected = choice;
    }
}

public sealed class ProxyProfilesViewModel : ViewModelBase
{
    public const string ScopeNotice = "仅用于订阅/EPG下载；原生代理播放尚未支持，配置代理的频道播放会报错而非直连。";
    public const string RefreshNotice = "档案与分配仅保存配置，不表示连接成功。变更后请重新选择来源或刷新频道/EPG。";
    private readonly ILibraryService _library;
    private readonly Func<Task>? _afterCommit;
    private ProxyProfileDraft? _draft;
    private bool _busy;
    private string? _message;
    public ObservableCollection<ProxyProfileItem> Profiles { get; } = new();
    public ObservableCollection<SourceProxyAssignment> Sources { get; } = new();
    public ProxyProfileDraft? Draft { get => _draft; private set { if (SetProperty(ref _draft, value)) OnPropertyChanged(nameof(IsEditing)); } }
    public bool IsEditing => Draft is not null;
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }
    public RelayCommand AddCommand { get; }
    public AsyncRelayCommand EditCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand AssignCommand { get; }
    public RelayCommand CancelAssignmentCommand { get; }

    public ProxyProfilesViewModel(ILibraryService library, Func<Task>? afterCommit = null)
    {
        _library = library; _afterCommit = afterCommit;
        AddCommand = new RelayCommand(() => { if (!IsBusy) { Message = null; Draft = new ProxyProfileDraft(); } });
        CancelCommand = new RelayCommand(() => { if (!IsBusy) { Draft = null; Message = null; } });
        CancelAssignmentCommand = new RelayCommand(parameter => { if (!IsBusy && parameter is SourceProxyAssignment row) row.Cancel(); });
        EditCommand = new AsyncRelayCommand(async parameter =>
        {
            if (IsBusy || parameter is not ProxyProfileItem item) return;
            IsBusy = true; Message = null;
            try
            {
                var current = (await _library.GetUserProxiesAsync()).Single(proxy => proxy.Id == item.Id);
                Draft = new ProxyProfileDraft(current);
            }
            catch { Message = "读取代理档案失败，请刷新后重试"; }
            finally { IsBusy = false; }
        });
        SaveCommand = new AsyncRelayCommand(async () =>
        {
            if (IsBusy || Draft is null) return;
            IsBusy = true; Message = null;
            try
            {
                var current = Draft.Id is null ? null : (await _library.GetUserProxiesAsync()).Single(proxy => proxy.Id == Draft.Id);
                UserProxy updated;
                try { updated = Draft.Apply(current); }
                catch (ArgumentException) { Message = "请填写名称和有效端点，仅支持 http://host:port 或 socks5://host:port（无认证/路径/query）；替换认证时请填写新值"; return; }
                await _library.SaveUserProxyAsync(updated);
                Draft = null;
                var index = Profiles.ToList().FindIndex(profile => profile.Id == updated.Id);
                if (index < 0) Profiles.Add(new ProxyProfileItem(updated));
                else Profiles[index] = new ProxyProfileItem(updated);
                await CommitRefreshAsync("代理档案已保存。");
            }
            catch { Message = "代理档案保存失败，请检查存储后重试"; }
            finally { IsBusy = false; }
        });
        DeleteCommand = new AsyncRelayCommand(async parameter =>
        {
            if (IsBusy || parameter is not ProxyProfileItem item) return;
            IsBusy = true; Message = null;
            try
            {
                var context = await _library.GetProxyPolicyContextAsync();
                var references = context.Sources.Where(source => source.ProxyId == item.Id).Select(source => source.Name).ToArray();
                if (references.Length > 0) { Message = "档案仍被来源引用，请先分配为 System 或其他档案：" + string.Join("、", references); return; }
                await _library.DeleteUserProxyAsync(item.Id);
                if (Draft?.Id == item.Id) Draft = null;
                var displayed = Profiles.FirstOrDefault(profile => profile.Id == item.Id);
                if (displayed is not null) Profiles.Remove(displayed);
                await CommitRefreshAsync("代理档案已删除。");
            }
            catch { Message = "删除失败：档案可能仍被来源/频道引用，或存储不可用。请先取消引用后重试"; }
            finally { IsBusy = false; }
        });
        AssignCommand = new AsyncRelayCommand(async parameter =>
        {
            if (IsBusy || parameter is not SourceProxyAssignment row || row.Selected is null) return;
            IsBusy = true; Message = null;
            try
            {
                var selected = row.Selected;
                await _library.SetSourceProxyAsync(row.SourceId, selected.Id);
                row.MarkSaved(selected);
                await CommitRefreshAsync("来源代理分配已保存。");
            }
            catch { row.Cancel(); Message = "来源代理分配保存失败，请刷新后重试"; }
            finally { IsBusy = false; }
        });
    }

    private async Task CommitRefreshAsync(string committedMessage)
    {
        try
        {
            if (_afterCommit is not null) await _afterCommit();
            else await RefreshAsync();
            Message = committedMessage + RefreshNotice;
        }
        catch
        {
            // Persistence already committed. A failing reload must not report a rollback.
            try { await RefreshAsync(); } catch { }
            Message = committedMessage + "但刷新失败，当前频道或显示可能未更新，请重新加载。" + RefreshNotice;
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var context = await _library.GetProxyPolicyContextAsync(ct);
        var profiles = context.Proxies.Select(proxy => new ProxyProfileItem(proxy)).ToArray();
        var choices = new[] { new ProxyChoice(null, "System（系统默认）") }.Concat(context.Proxies.Select(proxy => new ProxyChoice(proxy.Id, proxy.Name))).ToArray();
        var sources = context.Sources.OrderBy(source => source.Order).Select(source => new SourceProxyAssignment(source, choices)).ToArray();
        Profiles.Clear(); foreach (var profile in profiles) Profiles.Add(profile);
        Sources.Clear(); foreach (var source in sources) Sources.Add(source);
    }

    public static string EndpointSummary(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return "端点已隐藏";
        return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    public static void ValidateEndpoint(string? endpoint, ProxyKind kind)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath is not ("" or "/") || uri.Port is < 1 or > 65535 ||
            uri.Scheme != (kind == ProxyKind.Http ? "http" : "socks5")) throw new ArgumentException();
        // Require an explicit port, including the conventional HTTP port.
        var authority = endpoint![(endpoint.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0];
        var hostEnd = authority.LastIndexOf(':');
        if (hostEnd < 0 || (authority.StartsWith('[') && hostEnd < authority.IndexOf(']')) || !int.TryParse(authority[(hostEnd + 1)..], out _)) throw new ArgumentException();
    }
}
