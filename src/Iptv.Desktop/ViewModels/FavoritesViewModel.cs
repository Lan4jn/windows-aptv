using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Models;
using Iptv.Desktop.Services;

namespace Iptv.Desktop.ViewModels;

public sealed class FavoritesViewModel : ViewModelBase
{
    private readonly ILibraryService _libraryService;
    private readonly IPlayerSession _player;

    public ObservableCollection<Channel> FavoriteChannels { get; } = new();

    public AsyncRelayCommand PlayChannelCommand { get; }
    public AsyncRelayCommand RemoveFavoriteCommand { get; }
    public AsyncRelayCommand RefreshFavoritesCommand { get; }

    public FavoritesViewModel(ILibraryService libraryService, IPlayerSession player)
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

        RemoveFavoriteCommand = new AsyncRelayCommand(async param =>
        {
            if (param is Channel channel)
            {
                await _libraryService.RemoveFavoriteAsync(channel.Id);
                FavoriteChannels.Remove(channel);
            }
        });

        RefreshFavoritesCommand = new AsyncRelayCommand(() => RefreshFavoritesAsync());
    }

    public async Task RefreshFavoritesAsync(CancellationToken ct = default)
    {
        var list = await _libraryService.GetFavoritesAsync(ct);
        FavoriteChannels.Clear();
        foreach (var ch in list)
        {
            FavoriteChannels.Add(ch);
        }
    }
}
