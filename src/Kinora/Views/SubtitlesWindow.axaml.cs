using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kinora.ViewModels;

namespace Kinora.Views;

public partial class SubtitlesWindow : Window
{
    public SubtitlesWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SubtitlesViewModel vm)
                vm.CloseRequested += Close;
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SubtitlesViewModel vm && vm.DownloadCommand.CanExecute(null))
            vm.DownloadCommand.Execute(null);
    }
}
