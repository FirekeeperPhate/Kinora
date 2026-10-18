using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core.Subtitles;
using Kinora.Services;

namespace Kinora.ViewModels;

public sealed class SubtitleRow(SubtitleResult result)
{
    public SubtitleResult Result { get; } = result;
    public string Language => Format.Language(Result.Language.Replace('-', ' ').ToLowerInvariant());
    public string Release => Result.Release ?? Result.FileName;
    public string DownloadsText => Result.Downloads.ToString("N0", CultureInfo.CurrentCulture) + " downloads";
    public bool HashMatch => Result.HashMatch;
    public bool HearingImpaired => Result.HearingImpaired;
    public bool MachineTranslated => Result.MachineTranslated;
    public bool Forced => Result.Forced;
    public string? UploadedText => DateTime.TryParse(Result.Uploaded, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        ? d.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : null;
}

/// <summary>
/// Search OpenSubtitles for one video and save the chosen subtitle next to it. Searching is free;
/// each download counts against the account's daily quota, shown here.
/// </summary>
public partial class SubtitlesViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _s;
    private readonly IDialogs _dialogs;
    private readonly string _videoPath;
    private readonly SubtitleQuery _query;
    private readonly Func<string, Task> _saved;
    private readonly OpenSubtitlesClient _client;
    private readonly List<string> _languages;
    private List<SubtitleResult> _all = [];
    private string? _hash;

    public SubtitlesViewModel(AppServices services, IDialogs dialogs, string title, string videoPath, SubtitleQuery query, Func<string, Task> saved)
    {
        _s = services;
        _dialogs = dialogs;
        _videoPath = videoPath;
        _query = query;
        _saved = saved;
        _client = new OpenSubtitlesClient(_s.Settings.OpenSubtitlesKey);
        _languages = SubtitleFiles.ParseLanguages(_s.Settings.SubtitleLanguages);
        Title = title;
        FileName = Path.GetFileName(videoPath);
        LanguageChoices = _languages.Count > 0
            ? [$"{string.Join(", ", _languages)}", "All languages"]
            : ["All languages"];
        _languageChoice = LanguageChoices[0];
        _ = SearchAsync();
    }

    public string Title { get; }
    public string FileName { get; }
    public IReadOnlyList<string> LanguageChoices { get; }
    public ObservableCollection<SubtitleRow> Results { get; } = [];

    private string _languageChoice;

    public string LanguageChoice
    {
        get => _languageChoice;
        set
        {
            if (SetProperty(ref _languageChoice, value))
                _ = SearchAsync();
        }
    }

    private bool AllLanguages => LanguageChoice == "All languages";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial SubtitleRow? Selected { get; set; }

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial string? AccountText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial bool IsBusy { get; private set; }

    public event Action? CloseRequested;

    private async Task SearchAsync()
    {
        IsBusy = true;
        Message = null;
        Results.Clear();
        try
        {
            // Two small reads of the file give the hash; a NAS that sleeps gets a deadline
            if (_hash is null && File.Exists(_videoPath))
            {
                try
                {
                    _hash = await Task.Run(() => MovieHash.Compute(_videoPath)).WaitAsync(TimeSpan.FromSeconds(20));
                }
                catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
                {
                }
            }
            var found = await _client.SearchAsync(_query with { MovieHash = _hash }, AllLanguages ? [] : _languages, CancellationToken.None);
            if (_s.Settings.SkipMachineTranslated)
                found = found.Where(r => !r.MachineTranslated).ToList();
            _all = SubtitleFiles.Rank(found, _languages);
            foreach (var r in _all.Take(100))
                Results.Add(new SubtitleRow(r));
            Selected = Results.FirstOrDefault();
            Message = Results.Count == 0
                ? AllLanguages ? "No subtitles found for this video." : "None in these languages. Try “All languages”."
                : null;
        }
        catch (Exception e) when (e is OpenSubtitlesException or HttpRequestException or TaskCanceledException)
        {
            Message = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task Download()
    {
        if (Selected is not { } row)
            return;
        IsBusy = true;
        Message = null;
        try
        {
            if (!_client.IsLoggedIn && !string.IsNullOrEmpty(_s.Settings.OpenSubtitlesUser))
            {
                var account = await _client.LoginAsync(_s.Settings.OpenSubtitlesUser, _s.Settings.OpenSubtitlesPassword, CancellationToken.None);
                AccountText = $"{account.RemainingDownloads:N0} downloads left today";
            }
            var target = SubtitleFiles.TargetPath(_videoPath, row.Result, row.Result.FileName);
            if (File.Exists(target)
                && !await _dialogs.ConfirmAsync("Subtitle already there",
                    $"{Path.GetFileName(target)} already exists.\n\nThe one there is kept as “.old” and the new one takes its name.", "Save"))
                return;

            var file = await _client.DownloadAsync(row.Result.FileId, CancellationToken.None);
            // The download may name the real format (.ass, .vtt) better than the search did
            var named = SubtitleFiles.TargetPath(_videoPath, row.Result, file.FileName);
            if (!string.Equals(Path.GetExtension(named), ".srt", StringComparison.OrdinalIgnoreCase))
                target = named;
            await Task.Run(() => SubtitleFiles.Save(target, file.Content));
            if (file.Remaining >= 0)
                AccountText = $"{file.Remaining:N0} downloads left today";
            await _saved(Path.GetFileName(target));
            Message = $"Saved as {Path.GetFileName(target)}.";
            CloseRequested?.Invoke();
        }
        catch (Exception e) when (e is OpenSubtitlesException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Message = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDownload() => Selected is not null && !IsBusy;

    public void Dispose() => _client.Dispose();
}
