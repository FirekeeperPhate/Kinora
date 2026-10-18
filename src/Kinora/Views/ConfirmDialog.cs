using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Kinora.Views;

/// <summary>A small yes/no window (Avalonia has no message box of its own).</summary>
public static class ConfirmDialog
{
    /// <param name="destructive">Enter and the first focus go to Cancel: deleting takes a click.</param>
    public static async Task<bool> ShowAsync(Window owner, string title, string message, string confirmText, bool destructive = false)
    {
        var result = false;
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = owner.Background,
            Icon = owner.Icon,
        };
        var confirm = new Button { Content = confirmText, Classes = { "accent" }, IsDefault = !destructive, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = destructive, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (destructive)
            window.Opened += (_, _) => cancel.Focus();
        confirm.Click += (_, _) => { result = true; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancel, confirm },
                },
            },
        };
        await window.ShowDialog(owner);
        return result;
    }
}
