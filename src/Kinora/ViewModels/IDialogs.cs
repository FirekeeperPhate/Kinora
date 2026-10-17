namespace Kinora.ViewModels;

/// <summary>What the view models ask of the window: dialogs and pickers.</summary>
public interface IDialogs
{
    Task ShowSettingsAsync(SettingsViewModel settings);
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
    Task<string?> PickFolderAsync(string title);
}
