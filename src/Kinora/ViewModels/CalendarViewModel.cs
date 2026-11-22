using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core.Arr;
using Kinora.Services;

namespace Kinora.ViewModels;

/// <summary>An episode or a movie of the calendar.</summary>
public sealed class CalendarEntry(ArrRelease release, CardViewModel? card, Action<CardViewModel> open)
{
    public ArrRelease Release { get; } = release;
    public CardViewModel? Card { get; } = card;
    public string Time => Release.HasTime ? Release.When.ToString("t", CultureInfo.CurrentCulture) : "";
    public string Title => Release.Title;
    public bool IsMovie => Release.Kind == ArrKind.Radarr;

    /// <summary>"S02E05 · The episode's title", or "Digital release".</summary>
    public string Detail => Release.EpisodeTitle is { Length: > 0 } t && t != "TBA" ? $"{Release.What} · {t}" : Release.What;

    public bool HasFile => Release.HasFile;
    public bool InLibrary => Card is not null;
    public string Tip => InLibrary ? $"Open “{Title}”" : $"“{Title}” isn't in the library yet";

    public void Open()
    {
        if (Card is not null)
            open(Card);
    }
}

public sealed record CalendarDay(string Header, string Date, bool IsToday, List<CalendarEntry> Entries);

/// <summary>
/// What comes out in the next weeks of what Sonarr and Radarr monitor: episodes at the hour they
/// air, movies on the day they come out. Asked each time the page opens.
/// </summary>
public partial class CalendarViewModel : ViewModelBase, IDisposable
{
    public static readonly int[] Spans = [7, 14, 28, 56];

    private readonly MainWindowViewModel _owner;
    private readonly AppServices _s;
    private CancellationTokenSource _load = new();

    public CalendarViewModel(MainWindowViewModel owner, AppServices services)
    {
        _owner = owner;
        _s = services;
        _ = LoadAsync();
    }

    public MainWindowViewModel Owner => _owner;

    public List<string> SpanOptions { get; } = ["Next 7 days", "Next 2 weeks", "Next 4 weeks", "Next 8 weeks"];

    [ObservableProperty]
    public partial int SpanIndex { get; set; } = 2;

    partial void OnSpanIndexChanged(int value) => _ = LoadAsync();

    [ObservableProperty]
    public partial List<CalendarDay> Days { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Nothing coming, or what went wrong asking.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    public bool HasMessage => Message is not null;

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public string Sources => (_s.Arr.HasSonarr, _s.Arr.HasRadarr) switch
    {
        (true, true) => "From Sonarr and Radarr, only what they monitor",
        (true, false) => "From Sonarr, only what it monitors",
        _ => "From Radarr, only what it monitors",
    };

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private async Task LoadAsync()
    {
        _load.Cancel();
        _load = new CancellationTokenSource();
        var ct = _load.Token;
        IsLoading = true;
        try
        {
            var days = Spans[Math.Clamp(SpanIndex, 0, Spans.Length - 1)];
            var today = DateTime.Today;
            var (releases, problems) = await _s.Arr.CalendarAsync(today, days, ct);
            if (ct.IsCancellationRequested)
                return;
            Days = releases.GroupBy(r => r.When.Date)
                           .Select(g => new CalendarDay(DayName(g.Key, today), g.Key.ToString("d MMMM", CultureInfo.InvariantCulture), g.Key == today,
                                                        g.Select(r => new CalendarEntry(r, _owner.CardFor(r), _owner.OpenTitle)).ToList()))
                           .ToList();
            var episodes = releases.Count(r => r.Kind == ArrKind.Sonarr);
            var movies = releases.Count(r => r.Kind == ArrKind.Radarr);
            var parts = new List<string>();
            if (_s.Arr.HasSonarr) parts.Add(Format.Count(episodes, "episode", "episodes"));
            if (_s.Arr.HasRadarr) parts.Add(Format.Count(movies, "movie release", "movie releases"));
            Summary = string.Join(" · ", parts);
            Message = problems.Count > 0 ? "Couldn't ask " + string.Join("; ", problems)
                    : releases.Count == 0 ? $"Nothing comes out in the {SpanOptions[SpanIndex].ToLowerInvariant()}."
                    : null;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoading = false;
        }
    }

    private static string DayName(DateTime day, DateTime today) =>
        day == today ? "Today"
        : day == today.AddDays(1) ? "Tomorrow"
        : day.ToString("dddd", CultureInfo.InvariantCulture);

    [RelayCommand]
    private static void Open(CalendarEntry entry) => entry.Open();

    [RelayCommand]
    private void Back() => _owner.GoBack();

    public void Dispose() => _load.Cancel();
}
