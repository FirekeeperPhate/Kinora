using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core;
using Kinora.Core.MediaServers;
using Kinora.Core.Models;
using Kinora.Core.Platform;
using Kinora.Core.Settings;
using Kinora.Services;

namespace Kinora.ViewModels;

/// <summary>What happens to a movie of a folder once it has been watched.</summary>
public sealed record MoveRule(long? RootId, string Text)
{
    public override string ToString() => Text;
}

public partial class RootItem(LibraryRoot root, Action<RootItem> moveRuleChanged) : ObservableObject
{
    private bool _filling;

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

    public IReadOnlyList<MoveRule> MoveRules { get; private set; } = [];
    public bool CanOfferMove => MoveRules.Count > 1;

    [ObservableProperty]
    public partial MoveRule? WatchedMove { get; set; }

    partial void OnWatchedMoveChanged(MoveRule? value)
    {
        if (_filling || value is null)
            return;
        Root.WatchedMoveRoot = value.RootId;
        moveRuleChanged(this);
    }

    /// <summary>The other movie folders, when the list of folders changes.</summary>
    public void SetMoveTargets(IEnumerable<LibraryRoot> others)
    {
        _filling = true;
        MoveRules = [new MoveRule(null, "Stays here"), .. others.Select(r => new MoveRule(r.Id, $"Offer to move it to {r.Name}"))];
        OnPropertyChanged(nameof(MoveRules));
        OnPropertyChanged(nameof(CanOfferMove));
        WatchedMove = MoveRules.FirstOrDefault(r => r.RootId == Root.WatchedMoveRoot) ?? MoveRules[0];
        _filling = false;
    }
}

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _s;
    private readonly IDialogs _dialogs;

    private readonly Func<Task<string>>? _checkUpdates;

    public SettingsViewModel(AppServices services, IDialogs dialogs, Func<Task<string>>? checkUpdates = null)
    {
        _s = services;
        _dialogs = dialogs;
        _checkUpdates = checkUpdates;
        CheckForUpdates = _s.Settings.CheckForUpdates;
        foreach (var root in _s.Database.GetRoots())
            Roots.Add(new RootItem(root, SaveMoveRule));
        RefreshMoveRules();
        ThemeIndex = (int)_s.Settings.Theme;
        WriteToMediaFolders = _s.Settings.WriteToMediaFolders;
        ScanOnStartup = _s.Settings.ScanOnStartup;
        UseBuiltInPlayer = _s.Settings.UseBuiltInPlayer;
        AutoPlayNextEpisode = _s.Settings.AutoPlayNextEpisode;
        CardWidth = _s.Settings.CardWidth;
        ScrollSpeed = _s.Settings.ScrollSpeed;
        TmdbKey = _s.Settings.TmdbKey;
        _originalKey = TmdbKey;
        _originalLanguage = _s.Settings.MetadataLanguage;
        MetadataLanguage = Languages.FirstOrDefault(l => l.Code == _s.Settings.MetadataLanguage)
                           ?? new MetadataLanguageOption(_s.Settings.MetadataLanguage, _s.Settings.MetadataLanguage);
        FetchAfterScan = _s.Settings.FetchAfterScan;
        LoadSubtitleSettings();
        LoadServerSettings();
    }

    private readonly string _originalKey;
    private readonly string _originalLanguage;

    /// <summary>A new key or language: worth asking TMDB again when the window closes.</summary>
    public bool OnlineChanged => TmdbKey.Trim() != _originalKey || MetadataLanguage.Code != _originalLanguage;

    public IReadOnlyList<MetadataLanguageOption> Languages { get; } =
    [
        new("en-US", "English"), new("it-IT", "Italian"), new("de-DE", "German"), new("fr-FR", "French"),
        new("es-ES", "Spanish"), new("pt-BR", "Portuguese (Brazil)"), new("nl-NL", "Dutch"), new("ja-JP", "Japanese"),
    ];

    [ObservableProperty]
    public partial string TmdbKey { get; set; } = "";

    [ObservableProperty]
    public partial MetadataLanguageOption MetadataLanguage { get; set; }

    [ObservableProperty]
    public partial bool FetchAfterScan { get; set; }

    [ObservableProperty]
    public partial string? KeyStatus { get; private set; }

    [ObservableProperty]
    public partial bool KeyOk { get; private set; }

    partial void OnTmdbKeyChanged(string value)
    {
        _s.Settings.TmdbKey = value;
        KeyStatus = null;
    }

    partial void OnMetadataLanguageChanged(MetadataLanguageOption value) => _s.Settings.MetadataLanguage = value.Code;
    partial void OnFetchAfterScanChanged(bool value) => _s.Settings.FetchAfterScan = value;

    [RelayCommand]
    private async Task TestKey()
    {
        if (TmdbKey.Trim().Length == 0)
        {
            KeyOk = false;
            KeyStatus = "Paste the key first.";
            return;
        }
        KeyStatus = "Checking…";
        try
        {
            using var client = new Kinora.Core.Online.TmdbClient(TmdbKey, MetadataLanguage.Code);
            KeyOk = await client.TestAsync(CancellationToken.None);
            KeyStatus = KeyOk ? "The key works." : "TMDB refused this key.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or Kinora.Core.Online.TmdbException)
        {
            KeyOk = false;
            KeyStatus = "Can't reach TMDB: " + e.Message;
        }
    }

    // ---- updates ----

    public string VersionText => $"Kinora {MainWindowViewModel.CurrentVersion.ToString(3)}";

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    public partial string? UpdateStatus { get; private set; }

    partial void OnCheckForUpdatesChanged(bool value) => _s.Settings.CheckForUpdates = value;

    [RelayCommand]
    private async Task CheckUpdatesNow()
    {
        if (_checkUpdates is null)
            return;
        UpdateStatus = "Checking…";
        UpdateStatus = await _checkUpdates();
    }

    [RelayCommand]
    private void OpenReleasesPage() => Launcher.OpenUrl(Kinora.Core.Updates.UpdateService.ReleasesPage);

    /// <summary>The notices shipped next to the program, or the ones on GitHub when running from the sources.</summary>
    [RelayCommand]
    private void OpenLicenses()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        if (File.Exists(local))
            Launcher.Open(local);
        else
            Launcher.OpenUrl($"https://github.com/{Kinora.Core.Updates.UpdateService.Repository}/blob/main/THIRD-PARTY-NOTICES.txt");
    }

    [RelayCommand]
    private void OpenTmdbApiPage() => Launcher.OpenUrl("https://www.themoviedb.org/settings/api");

    // ---- OpenSubtitles ----

    [ObservableProperty]
    public partial string OpenSubtitlesKey { get; set; } = "";

    [ObservableProperty]
    public partial string OpenSubtitlesUser { get; set; } = "";

    [ObservableProperty]
    public partial string OpenSubtitlesPassword { get; set; } = "";

    [ObservableProperty]
    public partial string SubtitleLanguages { get; set; } = "";

    [ObservableProperty]
    public partial bool SkipMachineTranslated { get; set; }

    [ObservableProperty]
    public partial string? SubtitlesStatus { get; private set; }

    partial void OnOpenSubtitlesKeyChanged(string value) { _s.Settings.OpenSubtitlesKey = value; SubtitlesStatus = null; }
    partial void OnOpenSubtitlesUserChanged(string value) { _s.Settings.OpenSubtitlesUser = value.Trim(); SubtitlesStatus = null; }
    partial void OnOpenSubtitlesPasswordChanged(string value) { _s.Settings.OpenSubtitlesPassword = value; SubtitlesStatus = null; }
    partial void OnSubtitleLanguagesChanged(string value) =>
        _s.Settings.SubtitleLanguages = string.Join(", ", Kinora.Core.Subtitles.SubtitleFiles.ParseLanguages(value));
    partial void OnSkipMachineTranslatedChanged(bool value) => _s.Settings.SkipMachineTranslated = value;

    private void LoadSubtitleSettings()
    {
        OpenSubtitlesKey = _s.Settings.OpenSubtitlesKey;
        OpenSubtitlesUser = _s.Settings.OpenSubtitlesUser ?? "";
        OpenSubtitlesPassword = _s.Settings.OpenSubtitlesPassword;
        SubtitleLanguages = _s.Settings.SubtitleLanguages;
        SkipMachineTranslated = _s.Settings.SkipMachineTranslated;
        SubtitlesStatus = null;
    }

    /// <summary>Logs in: proves key, user and password, and tells today's downloads.</summary>
    [RelayCommand]
    private async Task TestOpenSubtitles()
    {
        if (OpenSubtitlesKey.Trim().Length == 0)
        {
            SubtitlesStatus = "Paste the API key first.";
            return;
        }
        SubtitlesStatus = "Checking…";
        try
        {
            using var client = new Kinora.Core.Subtitles.OpenSubtitlesClient(OpenSubtitlesKey);
            if (OpenSubtitlesUser.Trim().Length == 0)
            {
                await client.SearchAsync(new Kinora.Core.Subtitles.SubtitleQuery(ImdbId: "tt0113277"), ["en"], CancellationToken.None);
                SubtitlesStatus = "The key works. Add user name and password to download.";
                return;
            }
            var account = await client.LoginAsync(OpenSubtitlesUser.Trim(), OpenSubtitlesPassword, CancellationToken.None);
            SubtitlesStatus = $"Logged in{(account.Vip ? " (VIP)" : "")} · {account.RemainingDownloads:N0} of {account.AllowedDownloads:N0} downloads left today.";
        }
        catch (Exception e) when (e is Kinora.Core.Subtitles.OpenSubtitlesException or HttpRequestException or TaskCanceledException)
        {
            SubtitlesStatus = e.Message;
        }
    }

    [RelayCommand]
    private void OpenOpenSubtitlesPage() => Launcher.OpenUrl("https://www.opensubtitles.com/en/consumers");

    // ---- Emby or Jellyfin ----

    private bool _loadingServer;

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = "";

    [ObservableProperty]
    public partial string ServerKey { get; set; } = "";

    [ObservableProperty]
    public partial string? ServerStatus { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ServerUser> ServerUsers { get; private set; } = [];

    [ObservableProperty]
    public partial ServerUser? ServerUser { get; set; }

    [ObservableProperty]
    public partial bool SyncOnStartup { get; set; }

    private void LoadServerSettings()
    {
        _loadingServer = true;
        ServerUrl = _s.Settings.MediaServerUrl ?? "";
        ServerKey = _s.Settings.MediaServerKey;
        SyncOnStartup = _s.Settings.SyncOnStartup;
        if (_s.Settings is { MediaServerUserId: { } id, MediaServerUserName: { } name })
        {
            ServerUsers = [new ServerUser(id, name)];
            ServerUser = ServerUsers[0];
        }
        _loadingServer = false;
    }

    partial void OnServerUrlChanged(string value)
    {
        if (_loadingServer)
            return;
        _s.Settings.MediaServerUrl = value.Trim();
        ServerStatus = null;
        ServerUser = null;
        ServerUsers = [];
    }

    partial void OnServerKeyChanged(string value)
    {
        if (_loadingServer)
            return;
        _s.Settings.MediaServerKey = value;
        ServerStatus = null;
    }

    partial void OnServerUserChanged(ServerUser? value)
    {
        if (_loadingServer || value?.Id == _s.Settings.MediaServerUserId)
            return;
        _s.Settings.MediaServerUserId = value?.Id;
        _s.Settings.MediaServerUserName = value?.Name;
        _s.Database.ClearSyncStates();   // another user has seen other things
    }

    partial void OnSyncOnStartupChanged(bool value)
    {
        if (!_loadingServer)
            _s.Settings.SyncOnStartup = value;
    }

    [RelayCommand]
    private async Task TestServer()
    {
        if (ServerUrl.Trim().Length == 0 || ServerKey.Trim().Length == 0)
        {
            ServerStatus = "Write the address and paste the key first.";
            return;
        }
        ServerStatus = "Checking…";
        try
        {
            using var client = new MediaServerClient(ServerUrl, ServerKey);
            var info = await client.ConnectAsync(CancellationToken.None);
            var users = await client.GetUsersAsync(CancellationToken.None);
            var wanted = _s.Settings.MediaServerUserId;
            // A new list empties the box for a moment: that isn't the user changing
            _loadingServer = true;
            ServerUsers = users;
            ServerUser = users.FirstOrDefault(u => u.Id == wanted) ?? (users.Count == 1 ? users[0] : null);
            _loadingServer = false;
            if (ServerUser is { } user)
                OnServerUserChanged(user);
            _s.Settings.MediaServerProduct = info.Kind.ToString();
            ServerStatus = $"{info.Kind} {info.Version} answers{(info.Name.Length > 0 ? $": {info.Name}" : "")}."
                           + (ServerUser is null ? " Choose whose watched list to sync." : "");
        }
        catch (Exception e) when (e is MediaServerException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            ServerStatus = e is MediaServerException ? e.Message : "Can't reach the server: " + e.Message;
        }
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

    /// <summary>0 = movies, 1 = TV shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRootIsShows))]
    public partial int NewRootKindIndex { get; set; }

    public bool NewRootIsShows => NewRootKindIndex == 1;

    [ObservableProperty]
    public partial string? AddError { get; private set; }

    partial void OnThemeIndexChanged(int value)
    {
        _s.Settings.Theme = (ThemeChoice)value;
        App.ApplyTheme(_s.Settings.Theme);
    }

    partial void OnWriteToMediaFoldersChanged(bool value) => _s.Settings.WriteToMediaFolders = value;
    partial void OnScanOnStartupChanged(bool value) => _s.Settings.ScanOnStartup = value;

    // ---- playback ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseSystemPlayer))]
    public partial bool UseBuiltInPlayer { get; set; }

    /// <summary>The other radio button: the two can't both be bound to one value.</summary>
    public bool UseSystemPlayer
    {
        get => !UseBuiltInPlayer;
        set => UseBuiltInPlayer = !value;
    }

    partial void OnUseBuiltInPlayerChanged(bool value) => _s.Settings.UseBuiltInPlayer = value;

    [ObservableProperty]
    public partial bool AutoPlayNextEpisode { get; set; }

    partial void OnAutoPlayNextEpisodeChanged(bool value) => _s.Settings.AutoPlayNextEpisode = value;

    public static string SystemPlayerText => OperatingSystem.IsWindows()
        ? "The app Windows opens videos with"
        : "The app the system opens videos with";

    public static string BuiltInPlayerNote =>
        "Inside the window, with audio and subtitle choice; starts again where you stopped and marks what you finished as watched. "
        + (OperatingSystem.IsWindows() ? "Uses VLC's engine, which comes with Kinora."
           : "Uses VLC's engine: VLC must be installed on this computer.");
    partial void OnCardWidthChanged(double value) => _s.Settings.CardWidth = Math.Round(value);

    [ObservableProperty]
    public partial double ScrollSpeed { get; set; }

    partial void OnScrollSpeedChanged(double value) => _s.Settings.ScrollSpeed = Math.Round(value * 2) / 2;

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
        var path = await _dialogs.PickFolderAsync(NewRootIsShows ? "Choose a folder with TV shows" : "Choose a folder with movies");
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
        var root = _s.Database.AddRoot(NewRootIsShows ? LibraryKind.Series : LibraryKind.Movies, name, path);
        Roots.Add(new RootItem(root, SaveMoveRule));
        RefreshMoveRules();
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
        RefreshMoveRules();
        RootsChanged = true;
    }

    private void SaveMoveRule(RootItem item) => _s.Database.SetWatchedMoveRoot(item.Root.Id, item.Root.WatchedMoveRoot);

    private void RefreshMoveRules()
    {
        foreach (var item in Roots)
            item.SetMoveTargets(item.Root.Kind != LibraryKind.Movies ? []
                : Roots.Where(r => r != item && r.Root.Kind == LibraryKind.Movies).Select(r => r.Root).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase));
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

public sealed record MetadataLanguageOption(string Code, string Name)
{
    public override string ToString() => $"{Name} ({Code})";
}
