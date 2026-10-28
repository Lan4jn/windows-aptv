using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Iptv.Core.Models;
using Iptv.Desktop;
using Iptv.Desktop.ViewModels;

namespace Iptv.Desktop.Tests.Integration;

public class ProxyProfilesUiTests
{
    [AvaloniaTheory]
    [InlineData(850)]
    [InlineData(1280)]
    public async Task ConfigurationPage_ProvidesBoundProxyActions_AndLocalLimitNotice(int width)
    {
        var source = new SourceConfig("source", "Synthetic source", SourceKind.M3u, "https://example.invalid/list");
        var library = new FakeLibraryService(new[] { source });
        await library.SaveUserProxyAsync(new UserProxy("proxy", "Fixture proxy", ProxyKind.Http, "http://[::1]:8080", "fixture-user", "fixture-password"));
        var vm = new MainWindowViewModel(new FakePlaylistService(), library, new FakeEpgService(), new FakePlayer());
        var window = new MainWindow { DataContext = vm, Width = width, Height = 600 };
        try
        {
            window.Show();
            await window.InitializationTask;
            vm.NavigateToConfigsCommand.Execute(null);
            window.UpdateLayout();
            var add = window.FindControl<Button>("AddProxyProfileButton");
            Assert.NotNull(add);
            Assert.NotNull(add.Command);
            Assert.True(add.Command.CanExecute(null));
            add.Command.Execute(null);
            window.UpdateLayout();
            var editor = window.FindControl<Border>("ProxyProfileEditor");
            Assert.NotNull(editor);
            Assert.True(editor.IsEffectivelyVisible);
            var endpoint = window.FindControl<TextBox>("ProxyEndpointInput");
            Assert.NotNull(endpoint);
            Assert.Equal("", endpoint.Text ?? "");
            var save = window.FindControl<Button>("SaveProxyProfileButton")!;
            Assert.NotNull(save.Command);
            var origin = save.TranslatePoint(default, window)!.Value;
            Assert.True(origin.X >= 240 && origin.X + save.Bounds.Width <= window.ClientSize.Width + .5);
            var cancel = window.FindControl<Button>("CancelProxyProfileButton")!;
            cancel.Command!.Execute(null);
            Assert.All(window.GetVisualDescendants().OfType<TextBox>(), input => Assert.DoesNotContain("fixture-password", input.Text ?? ""));
            Assert.All(window.GetVisualDescendants().OfType<TextBlock>(), text => Assert.DoesNotContain("fixture-user", text.Text ?? ""));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => (text.Text ?? "").Contains("原生代理播放尚未支持"));
            var proxyVm = vm.ConfigsVm.ProxyProfilesVm;
            var edit = window.GetVisualDescendants().OfType<Button>().Single(button => button.Command == proxyVm.EditCommand);
            await ((AsyncRelayCommand)edit.Command!).ExecuteAsync(edit.CommandParameter);
            window.UpdateLayout();
            Assert.Equal("", window.FindControl<TextBox>("ProxyUsernameInput")!.Text ?? "");
            Assert.Equal("", window.FindControl<TextBox>("ProxyPasswordInput")!.Text ?? "");
            Assert.Equal('●', window.FindControl<TextBox>("ProxyPasswordInput")!.PasswordChar);
            cancel.Command!.Execute(null);
            add.Command.Execute(null);
            window.FindControl<TextBox>("ProxyNameInput")!.Text = "Second proxy";
            window.FindControl<TextBox>("ProxyEndpointInput")!.Text = "http://proxy.invalid:8080";
            window.UpdateLayout();
            save.BringIntoView();
            window.UpdateLayout();
            var scroll = window.FindControl<ScrollViewer>("ConfigManagementScroll")!;
            Assert.True(scroll.Offset.Y > 0, "Editor must remain reachable by scrolling at minimum height");
            var viewportTop = scroll.TranslatePoint(default, window)!.Value.Y;
            var saveTop = save.TranslatePoint(default, window)!.Value.Y;
            Assert.InRange(saveTop, viewportTop - .5, viewportTop + scroll.Bounds.Height);
            await ((AsyncRelayCommand)save.Command!).ExecuteAsync(null);
            Assert.Equal(2, (await library.GetUserProxiesAsync()).Count);
            window.UpdateLayout();
            var row = proxyVm.Sources.Single();
            var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(combo => ReferenceEquals(combo.DataContext, row));
            picker.SelectedItem = row.Choices.Single(choice => choice.Name == "Second proxy");
            var cancelAssign = window.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.DataContext, row) && button.Command == proxyVm.CancelAssignmentCommand);
            cancelAssign.Command!.Execute(cancelAssign.CommandParameter);
            Assert.Null(Assert.Single(await library.GetConfigsAsync()).ProxyId);
            picker.SelectedItem = row.Choices.Single(choice => choice.Name == "Second proxy");
            var assign = window.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.DataContext, row) && button.Command == proxyVm.AssignCommand);
            assign.BringIntoView();
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => ReferenceEquals(text.DataContext, row) && (text.Text ?? "").Contains("原生代理播放尚未支持"));
            await ((AsyncRelayCommand)assign.Command!).ExecuteAsync(assign.CommandParameter);
            Assert.Equal(row.Choices.Single(choice => choice.Name == "Second proxy").Id, Assert.Single(await library.GetConfigsAsync()).ProxyId);
            Assert.Contains("分配已保存", proxyVm.Message);

        }
        finally { window.Close(); }
    }
}
