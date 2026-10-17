using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core;
using Kinora.Core.Models;
using Kinora.Core.Platform;
using Kinora.Core.Settings;
using Kinora.Services;

namespace Kinora.ViewModels;

public partial class RootItem(LibraryRoot root) : ObservableObject
{
    public LibraryRoot Root { get; } = root;
    public string Name => Root.Name;
    public string KindText => Root.Kind == LibraryKind.Movies ? "Movies" : "TV shows";

    public string Path
    {
        get => Root.Path;
        set
        {
            Root.Path = value;
            OnPropertyChanged();
        }
    }
}

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _s;
    private readonly IDialogs _dialogs;

    public SettingsViewModel(AppServices services, IDialogs dialogs)
    {
        _s = services;
        _dialogs = dialogs;
        foreach (var root in _s.Database.GetRoots())
            Roots.Add(new RootItem(root));
        ThemeIndex = (int)_s.Settings.Theme;
        WriteToMediaFolders = _s.Settings.WriteToMediaFolders;
        ScanOnStartup = _s.Settings.ScanOnStartup;
        CardWidth = _s.Settings.CardWidth;
    }

    public ObservableCollection<RootItem> Roots { get; } = [];
    public bool RootsChanged { get; private set; }
    public string DataFolder => AppPaths.DataDir;

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    public partial bool WriteToMediaFolders { get; set; }

    [ObservableProperty]
    public partial bool ScanOnStartup { get; set; }

    [ObservableProperty]
    public partial double CardWidth { get; set; }

    [ObservableProperty]
    public partial string NewRootPath { get; set; } = "";

    [ObservableProperty]
    public partial string NewRootName { get; set; } = "";

    [ObservableProperty]
    public partial string? AddError { get; private set; }

    partial void OnThemeIndexChanged(int value)
    {
        _s.Settings.Theme = (ThemeChoice)value;
        App.ApplyTheme(_s.Settings.Theme);
    }

    partial void OnWriteToMediaFoldersChanged(bool value) => _s.Settings.WriteToMediaFolders = value;
    partial void OnScanOnStartupChanged(bool value) => _s.Settings.ScanOnStartup = value;
    partial void OnCardWidthChanged(double value) => _s.Settings.CardWidth = Math.Round(value);

    partial void OnNewRootPathChanged(string value)
    {
        AddError = null;
        if (NewRootName.Length == 0 || NewRootName == DefaultName(_lastAutoPath))
            NewRootName = DefaultName(value);
        _lastAutoPath = value;
    }

    private string _lastAutoPath = "";

    private static string DefaultName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }

    [RelayCommand]
    private async Task Browse()
    {
        var path = await _dialogs.PickFolderAsync("Choose a folder with movies");
        if (path is not null)
            NewRootPath = path;
    }

    [RelayCommand]
    private async Task AddRoot()
    {
        var path = NewRootPath.Trim().Trim('"');
        if (path.Length == 0)
        {
            AddError = "Choose or type the folder first.";
            return;
        }
        if (Roots.Any(r => string.Equals(r.Path.TrimEnd('\\', '/'), path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
        {
            AddError = "This folder is already in the library.";
            return;
        }
        var exists = await _s.Scanner.IsReachableAsync(path, CancellationToken.None);
        if (!exists)
        {
            AddError = "Can't reach this folder. Check the path, or that the NAS is on.";
            return;
        }
        var name = NewRootName.Trim().Length > 0 ? NewRootName.Trim() : DefaultName(path);
        var root = _s.Database.AddRoot(LibraryKind.Movies, name, path);
        Roots.Add(new RootItem(root));
        RootsChanged = true;
        NewRootPath = "";
        NewRootName = "";
        AddError = null;
    }

    [RelayCommand]
    private async Task RemoveRoot(RootItem item)
    {
        var ok = await _dialogs.ConfirmAsync("Remove folder",
            $"Remove “{item.Name}” from the library?\n\nOnly Kinora forgets it: the files on disk are not touched.",
            "Remove");
        if (!ok)
            return;
        _s.Database.RemoveRoot(item.Root.Id);
        Roots.Remove(item);
        RootsChanged = true;
    }

    /// <summary>For a share that is now mounted somewhere else: the library stays as it is.</summary>
    [RelayCommand]
    private async Task ChangeRootPath(RootItem item)
    {
        var path = await _dialogs.PickFolderAsync($"New location of “{item.Name}”");
        if (path is null)
            return;
        item.Path = path;
        _s.Database.UpdateRoot(item.Root);
        RootsChanged = true;
    }

    [RelayCommand]
    private void OpenDataFolder() => Launcher.Reveal(AppPaths.DataDir);
}
