using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Iptv.Core.Models;
using Iptv.Desktop;
using Iptv.Desktop.Services;
using Iptv.Desktop.ViewModels;

namespace Iptv.Desktop.Tests.Integration;

public class SmallWindowAndEmptySourceTests
{
    [AvaloniaTheory]
    [InlineData(850, 600, 1)]
    [InlineData(1280, 800, 1)]
    [InlineData(850, 600, 2)]
    public async Task ToolbarControls_FitWithoutOverlap(double width, double height, double scale)
    {
        var vm = CreateVm();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        try
        {
            SetScaling(window, scale);
            window.Show();
            await window.InitializationTask;
            window.UpdateLayout();
            Assert.Equal(scale, window.RenderScaling);
            var source = window.FindControl<ComboBox>("SourcePicker")!;
            var filter = window.GetVisualDescendants().OfType<TextBox>().Single(c => c.DataContext == vm.ChannelsVm && c.Watermark?.StartsWith("过滤当前频道") == true);
            var refresh = window.GetVisualDescendants().OfType<Button>().Single(c => c.Command == vm.ChannelsVm.RefreshCommand);
            var group = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.DataContext == vm.ChannelsVm && c != source);
            var controls = new Control[] { source, filter, refresh, group };
            var rects = controls.Select(c => new Rect(c.TranslatePoint(default, window)!.Value, c.Bounds.Size)).ToArray();
            foreach (var rect in rects)
            {
                Assert.True(rect.Width > 40 && rect.Height > 15, $"Unusable control bounds {rect}");
                Assert.True(rect.X >= 240 && rect.Y >= 0 && rect.Right <= window.ClientSize.Width + 0.5 && rect.Bottom <= window.ClientSize.Height, $"Outside client: {rect}, {window.ClientSize}");
            }
            for (var i = 0; i < rects.Length; i++)
                for (var j = i + 1; j < rects.Length; j++)
                    Assert.False(rects[i].Intersects(rects[j]), $"Overlapping toolbar controls {rects[i]} / {rects[j]}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ZeroSource_HasVisibleAction_OpeningM3uForm()
    {
        var vm = CreateVm();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            await window.InitializationTask;
            window.UpdateLayout();
            var guidance = window.FindControl<Border>("NoSourcesGuidance");
            Assert.NotNull(guidance);
            Assert.True(guidance.IsEffectivelyVisible);
            var button = window.FindControl<Button>("AddFirstSourceButton")!;
            Assert.True(button.IsEffectivelyVisible);
            Assert.True(button.Command!.CanExecute(null));
            button.Command.Execute(null);
            Assert.Equal(NavigationSection.Configs, vm.CurrentSection);
            Assert.True(vm.ConfigsVm.IsAddDialogOpen);
            Assert.Equal(SourceKind.M3u, vm.ConfigsVm.AddForm.Kind);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredSource_DoesNotShowNoSourceGuidance_EvenIfEmptyOrFailed(bool fail)
    {
        var config = new SourceConfig("source", "Synthetic source", SourceKind.M3u, "https://example.invalid/list");
        var vm = CreateVm(new[] { config }, playlist: fail ? new FailingPlaylist() : new FakePlaylistService());
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            await window.InitializationTask;
            window.UpdateLayout();
            var guidance = window.FindControl<Border>("NoSourcesGuidance");
            Assert.NotNull(guidance);
            Assert.False(guidance.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DeletingLastSource_RestoresGuidance()
    {
        var config = new SourceConfig("source", "Synthetic source", SourceKind.M3u, "https://example.invalid/list");
        var vm = CreateVm(new[] { config });
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            await window.InitializationTask;
            vm.ConfigsVm.ConfirmDeleteAsync = _ => Task.FromResult(true);
            await vm.ConfigsVm.DeleteConfigCommand.ExecuteAsync(vm.ConfigsVm.Configs.Single());
            window.UpdateLayout();
            Assert.Empty(vm.ConfigsVm.Configs);
            Assert.True(window.FindControl<Border>("NoSourcesGuidance")?.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LongGroupBadge_IsBoundedEllipsis_WithFullTooltip()
    {
        var group = string.Concat(Enumerable.Repeat("Synthetic long group ", 15));
        var channel = new Channel("one", "source", "Synthetic channel", "https://example.invalid/live", group: group);
        var config = new SourceConfig("source", "Synthetic source", SourceKind.M3u, "https://example.invalid/list");
        var vm = CreateVm(new[] { config }, new FakePlaylistService(new[] { channel }));
        var window = new MainWindow { DataContext = vm, Width = 850, Height = 600 };
        try
        {
            window.Show();
            await window.InitializationTask;
            window.UpdateLayout();
            var badge = window.GetVisualDescendants().OfType<TextBlock>().Single(t => ReferenceEquals(t.DataContext, channel) && t.Text == group);
            Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, badge.TextTrimming);
            Assert.Equal(group, ToolTip.GetTip(badge));
            Assert.True(badge.Bounds.Width <= 145, $"Badge grew beyond card: {badge.Bounds}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SourceListReadFailure_DoesNotPretendNoSources()
    {
        var vm = new MainWindowViewModel(new FakePlaylistService(), new UnreadableLibrary(), new FakeEpgService(), new FakePlayer());
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            await window.InitializationTask;
            window.UpdateLayout();
            Assert.False(window.FindControl<Border>("NoSourcesGuidance")!.IsVisible);
            Assert.Contains("恢复失败", window.FindControl<TextBlock>("ChannelsStatus")!.Text);
        }
        finally { window.Close(); }
    }

    private sealed class UnreadableLibrary : FakeLibraryService, ILibraryService
    {
        Task<IReadOnlyList<SourceConfig>> ILibraryService.GetConfigsAsync(CancellationToken ct)
            => Task.FromException<IReadOnlyList<SourceConfig>>(new IOException("Synthetic read failure"));
    }

    private static MainWindowViewModel CreateVm(IEnumerable<SourceConfig>? sources = null, IPlaylistService? playlist = null)
        => new(playlist ?? new FakePlaylistService(), new FakeLibraryService(sources), new FakeEpgService(), new FakePlayer());

    private static void SetScaling(MainWindow window, double scale)
    {
        var impl = typeof(TopLevel).GetProperty("PlatformImpl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(window)!;
        impl.GetType().GetField("<RenderScaling>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(impl, scale);
        var callback = (Action<double>?)impl.GetType().GetProperty("ScalingChanged")!.GetValue(impl);
        callback?.Invoke(scale);
    }

    private sealed class FailingPlaylist : IPlaylistService
    {
        public Task<PlaylistChannelsResult> LoadChannelsAsync(SourceConfig config, CancellationToken ct = default)
            => Task.FromException<PlaylistChannelsResult>(new InvalidOperationException("Synthetic failure"));
    }
}
