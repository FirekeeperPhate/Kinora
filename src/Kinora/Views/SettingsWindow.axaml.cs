using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Kinora.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Services.UiScale.Attach(this, growWindow: true);
    }

    private void OnDone(object? sender, RoutedEventArgs e) => Close();
}
