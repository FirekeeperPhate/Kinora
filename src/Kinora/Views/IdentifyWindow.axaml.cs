using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kinora.ViewModels;

namespace Kinora.Views;

public partial class IdentifyWindow : Window
{
    public IdentifyWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is IdentifyViewModel vm)
                vm.CloseRequested += Close;
        };
        Opened += (_, _) => QueryBox.Focus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is IdentifyViewModel vm && vm.ChooseCommand.CanExecute(null))
            vm.ChooseCommand.Execute(null);
    }
}
