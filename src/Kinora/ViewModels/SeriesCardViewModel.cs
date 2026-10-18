using System.Text;
using Kinora.Core;
using Kinora.Core.Models;
using Kinora.Services;

namespace Kinora.ViewModels;

public sealed class SeriesCardViewModel : CardViewModel
{
    private readonly MainWindowViewModel _owner;

    public SeriesCardViewModel(MainWindowViewModel owner, PosterLoader loader, Series series, string castNames) : base(loader)
    {
        _owner = owner;
        Series = series;
        CastNames = castNames;
        Refresh();
    }

    public Series Series { get; private set; }
    public string CastNames { get; private set; }

    public override long Id => Series.Id;
    public override long RootId => Series.RootId;
    public override string Title => Series.Title;
    public override int? Year => Series.Year;
    public override double? Rating => Series.Rating;

    /// <summary>For "recently added" and the date order, a show is as new as its newest episode.</summary>
    public override long DateAdded => Math.Max(Series.LastEpisodeAdded, Series.DateAdded);
    public override bool NeedsMatch => Series.NeedsMatch;
    public override IReadOnlyList<string> Genres => Series.GenreList;
    protected override string? ThumbKey => Series.ThumbKey;
    public override bool Watched => Series.EpisodeCount > 0 && Series.WatchedCount >= Series.EpisodeCount;
    public override int UnwatchedCount => Series.EpisodeCount - Series.WatchedCount;
    public override string WatchedMenuText => Watched ? "Mark all as unwatched" : "Mark all as watched";

    public override string Subtitle
    {
        get
        {
            var years = Series.Year is { } y
                ? Series.EndYear is { } e && e != y ? $"{y}–{e}" : Series.Status is "Ended" or "Canceled" ? y.ToString() : $"{y}–"
                : null;
            var count = Format.Count(Series.EpisodeCount, "episode", "episodes");
            return years is null ? count : $"{years}  ·  {count}";
        }
    }

    public void Update(Series series, string castNames)
    {
        var posterChanged = series.ThumbKey != Series.ThumbKey;
        Series = series;
        CastNames = castNames;
        Refresh();
        Refreshed(posterChanged);
    }

    /// <summary>After episodes were marked watched on the show page.</summary>
    public void SetWatchedCount(int watched)
    {
        Series.WatchedCount = watched;
        NotifyWatchedChanged();
    }

    private void Refresh()
    {
        var s = Series;
        SearchKey = SearchText.Normalize(new StringBuilder()
            .Append(s.Title).Append(' ').Append(s.OriginalTitle).Append(' ').Append(s.Year).Append(' ')
            .Append(s.Studios.Replace('|', ' ')).Append(' ').Append(CastNames)
            .ToString());
        SortKey = SortTitle(s.SortTitle ?? s.Title);
    }

    protected override void Open() => _owner.Activate(this);
    protected override void Play() => _owner.PlayNextEpisode(this);
    protected override void Reveal() => _owner.Reveal(this);
    protected override void ToggleWatched() => _owner.SetSeriesWatched(this, !Watched);
    protected override Task Identify() => _owner.IdentifyAsync(this);
}
