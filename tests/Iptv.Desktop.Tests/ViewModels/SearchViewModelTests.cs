using System.Collections.Generic;
using System.Threading.Tasks;
using Iptv.Core.Models;
using Iptv.Desktop.Services;
using Iptv.Desktop.ViewModels;
using Xunit;

namespace Iptv.Desktop.Tests.ViewModels;

public class SearchViewModelTests
{
    private readonly List<Channel> _testChannels = new()
    {
        new("ch1", "s1", "CCTV-1 综合", "http://cctv1.m3u8"),
        new("ch2", "s1", "CCTV-5 体育", "http://cctv5.m3u8"),
        new("ch3", "s1", "凤凰中文", "http://fh.m3u8"),
    };

    [Fact]
    public void InitialState_ShowsEmptyGuidance()
    {
        var library = new FakeLibraryService();
        var player = new FakePlayer();
        var vm = new SearchViewModel(library, player);

        Assert.True(vm.IsEmpty);
        Assert.Equal("请输入您想要搜索的频道名称", vm.EmptyGuidanceTitle);
        Assert.Equal("不区分大小写", vm.EmptyGuidanceSubtitle);
        Assert.Empty(vm.SearchResults);
    }

    [Theory]
    [InlineData("cctv", 2)]
    [InlineData("CCTV", 2)]
    [InlineData("体育", 1)]
    [InlineData("凤凰", 1)]
    [InlineData("NotExist", 0)]
    public async Task SearchText_PerformsCaseInsensitiveFiltering(string query, int expectedCount)
    {
        var library = new FakeLibraryService(allChannels: _testChannels);
        var player = new FakePlayer();
        var vm = new SearchViewModel(library, player);

        vm.SearchText = query;
        await vm.PerformSearchAsync();

        Assert.False(vm.IsEmpty);
        Assert.Equal(expectedCount, vm.SearchResults.Count);
    }
}
