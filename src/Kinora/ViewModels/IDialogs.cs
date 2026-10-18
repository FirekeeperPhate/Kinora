namespace Kinora.ViewModels;

/// <summary>What the view models ask of the window: dialogs and pickers.</summary>
public interface IDialogs
{
    Task ShowSettingsAsync(SettingsViewModel settings);
    /// <summary>Asks yes or no; <paramref name="destructive"/>: Enter answers Cancel, not the action.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false);
    Task<string?> PickFolderAsync(string title);

    /// <summary>The TMDB id the user chose, or null.</summary>
    Task<string?> ShowIdentifyAsync(IdentifyViewModel identify);

    Task ShowSubtitlesAsync(SubtitlesViewModel subtitles);
}
