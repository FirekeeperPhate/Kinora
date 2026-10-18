using System.Text;
using Kinora.Core;
using Kinora.Core.Models;
using Kinora.Services;

namespace Kinora.ViewModels;

public sealed class MovieCardViewModel : CardViewModel
{
    private readonly MainWindowViewModel _owner;

    public MovieCardViewModel(MainWindowViewModel owner, PosterLoader loader, Movie movie, string castNames) : base(loader)
    {
        _owner = owner;
        Movie = movie;
        CastNames = castNames;
        Refresh();
    }

    public Movie Movie { get; private set; }
    public string CastNames { get; private set; }

    public override long Id => Movie.Id;
    public override long RootId => Movie.RootId;
    public override string Title => Movie.Title;
    public override int? Year => Movie.Year;
    public override double? Rating => Movie.Rating;
    public override long DateAdded => Movie.DateAdded;
    public override bool NeedsMatch => Movie.NeedsMatch;
    public override IReadOnlyList<string> Genres => Movie.GenreList;
    protected override string? ThumbKey => Movie.ThumbKey;
    public override bool Watched => Movie.Watched;
    public override string WatchedMenuText => Movie.Watched ? "Mark as unwatched" : "Mark as watched";

    public override string Subtitle
    {
        get
        {
            var parts = new List<string>(2);
            if (Movie.Year is { } year)
                parts.Add(year.ToString());
            if (Movie.Runtime is { } minutes)
                parts.Add(Format.Runtime(minutes));
            return string.Join("  ·  ", parts);
        }
    }

    public void Update(Movie movie, string castNames)
    {
        var posterChanged = movie.ThumbKey != Movie.ThumbKey;
        Movie = movie;
        CastNames = castNames;
        Refresh();
        Refreshed(posterChanged);
    }

    private void Refresh()
    {
        var m = Movie;
        SearchKey = SearchText.Normalize(new StringBuilder()
            .Append(m.Title).Append(' ').Append(m.OriginalTitle).Append(' ').Append(m.Year).Append(' ')
            .Append(m.Directors.Replace('|', ' ')).Append(' ').Append(m.Collection).Append(' ').Append(CastNames)
            .ToString());
        SortKey = SortTitle(m.SortTitle ?? m.Title);
    }

    protected override void Open() => _owner.Activate(this);
    protected override void Play() => _owner.Play(this, null);
    protected override void Reveal() => _owner.Reveal(this);
    protected override void ToggleWatched() => _owner.SetWatched(this, !Movie.Watched);
    protected override Task Identify() => _owner.IdentifyAsync(this);

    protected override Task Delete() => _owner.DeleteAsync(this);
}
