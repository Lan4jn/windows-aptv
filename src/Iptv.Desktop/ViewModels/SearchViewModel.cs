using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Models;
using Iptv.Desktop.Services;

namespace Iptv.Desktop.ViewModels;

public sealed class SearchViewModel : ViewModelBase
{
    private readonly ILibraryService _libraryService;
    private readonly IPlayerSession _player;
    private string _searchText = string.Empty;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _ = PerformSearchAsync();
            }
        }
    }

    public bool IsEmpty => string.IsNullOrWhiteSpace(_searchText);
    public string EmptyGuidanceTitle => "请输入您想要搜索的频道名称";
    public string EmptyGuidanceSubtitle => "不区分大小写";

    public ObservableCollection<Channel> SearchResults { get; } = new();

    public AsyncRelayCommand PlayChannelCommand { get; }

    public SearchViewModel(ILibraryService libraryService, IPlayerSession player)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _player = player ?? throw new ArgumentNullException(nameof(player));

        PlayChannelCommand = new AsyncRelayCommand(async param =>
        {
            if (param is Channel channel)
            {
                await _libraryService.AddHistoryAsync(channel);
                await _player.PlayAsync(channel);
            }
        });
    }

    public async Task PerformSearchAsync(CancellationToken ct = default)
    {
        OnPropertyChanged(nameof(IsEmpty));

        if (string.IsNullOrWhiteSpace(_searchText))
        {
            SearchResults.Clear();
            return;
        }

        var all = await _libraryService.GetAllChannelsAsync(ct);
        var matches = all
            .Where(c => c.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
            .ToList();

        SearchResults.Clear();
        foreach (var ch in matches)
        {
            SearchResults.Add(ch);
        }
    }
}
