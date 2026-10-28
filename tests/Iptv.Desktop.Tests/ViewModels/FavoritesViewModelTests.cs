using System.Collections.Generic;
using System.Threading.Tasks;
using Iptv.Core.Models;
using Iptv.Desktop.Services;
using Iptv.Desktop.ViewModels;
using Xunit;

namespace Iptv.Desktop.Tests.ViewModels;

public class FavoritesViewModelTests
{
    [Fact]
    public async Task LoadFavoritesAsync_PopulatesCollection()
    {
        var channels = new List<Channel>
        {
            new("ch1", "s1", "CCTV-1", "http://cctv1.m3u8"),
            new("ch2", "s1", "CCTV-2", "http://cctv2.m3u8")
        };
        var library = new FakeLibraryService();
        foreach (var ch in channels)
        {
            await library.AddFavoriteAsync(ch);
        }

        var player = new FakePlayer();
        var vm = new FavoritesViewModel(library, player);

        await vm.RefreshFavoritesAsync();

        Assert.Equal(2, vm.FavoriteChannels.Count);
        Assert.Equal("CCTV-1", vm.FavoriteChannels[0].Name);
    }

    [Fact]
    public async Task RemoveFavorite_RemovesFromCollectionAndLibrary()
    {
        var ch = new Channel("ch1", "s1", "CCTV-1", "http://cctv1.m3u8");
        var library = new FakeLibraryService();
        await library.AddFavoriteAsync(ch);

        var player = new FakePlayer();
        var vm = new FavoritesViewModel(library, player);
        await vm.RefreshFavoritesAsync();

        await vm.RemoveFavoriteCommand.ExecuteAsync(ch);

        Assert.Empty(vm.FavoriteChannels);
        Assert.False(await library.IsFavoriteAsync(ch.Id));
    }
}
