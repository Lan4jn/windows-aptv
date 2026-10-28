using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Iptv.Desktop;

internal static class WindowDialogService
{
    public static async Task<bool> ConfirmAsync(Window owner, string message)
    {
        var dialog = new Window
        {
            Title = "确认删除", Width = 360, Height = 180, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "取消" };
        var confirm = new Button { Content = "删除" };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { cancel, confirm } }
            }
        };
        return await dialog.ShowDialog<bool>(owner);
    }
}
