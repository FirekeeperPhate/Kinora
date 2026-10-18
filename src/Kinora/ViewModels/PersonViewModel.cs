using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core.Online;
using Kinora.Core.Platform;
using Kinora.Services;

namespace Kinora.ViewModels;

/// <summary>A title on a person page, with what the person did in it.</summary>
public sealed record PersonTitle(CardViewModel Card, string Role)
{
    public string Title => Card.Title;
    public string? YearText => Card.Year?.ToString(CultureInfo.InvariantCulture);
    public string KindText => Card is SeriesCardViewModel ? "TV show" : "Movie";
}

/// <summary>
/// An actor or director: photo and biography from TMDB, and every movie and show in the library
/// they are in. Without a TMDB key only the library part is there.
/// </summary>
public partial class PersonViewModel : ViewModelBase, IDisposable
{
    private readonly MainWindowViewModel _owner;
    private readonly AppServices _s;
    private readonly string? _knownId;
    private readonly bool _director;
    private bool _disposed;

    public PersonViewModel(MainWindowViewModel owner, AppServices services, string name, string? tmdbId, bool director, List<PersonTitle> titles)
    {
        _owner = owner;
        _s = services;
        _knownId = tmdbId;
        _director = director;
        Name = name;
        Titles = titles;
        _ = LoadAsync();
    }

    public string Name { get; }
    public List<PersonTitle> Titles { get; }
    public string TitlesText => $"In your library: {Format.Count(Titles.Count, "title", "titles")}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhoto))]
    public partial Bitmap? Photo { get; private set; }

    public bool HasPhoto => Photo is not null;

    [ObservableProperty]
    public partial string? Biography { get; private set; }

    [ObservableProperty]
    public partial string? Facts { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTmdbPage))]
    public partial string? TmdbUrl { get; private set; }

    public bool HasTmdbPage => TmdbUrl is not null;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    private async Task LoadAsync()
    {
        using var client = _s.CreateTmdbClient();
        if (client is null)
        {
            Biography = "Add a TMDB API key in Settings to see the biography and the photo.";
            return;
        }
        IsLoading = true;
        try
        {
            var id = _knownId ?? _s.Database.FindPersonId(Name) ?? await FindAsync(client);
            if (id is null)
            {
                Biography = "TMDB doesn't know this person by this name.";
                return;
            }
            TmdbUrl = $"https://www.themoviedb.org/person/{id}";

            var person = _s.Database.GetTmdbPerson(id);
            if (person is null || person.Language != client.Language)
            {
                person = await client.GetPersonAsync(id, CancellationToken.None);
                if (person is not null)
                    _s.Database.SaveTmdbPerson(person);
            }
            if (person is null || _disposed)
                return;

            Biography = person.Biography ?? "No biography on TMDB.";
            Facts = BuildFacts(person);
            if (person.ProfilePath is { } path)
            {
                var key = Core.Imaging.ImageCache.KeyForOnline(path);
                if (!_s.People.Has(key) && await TmdbImages.DownloadAsync(path, "w342", CancellationToken.None) is { } bytes)
                    _s.People.Save(key, bytes);
                if (_s.People.Has(key) && !_disposed)
                    Photo = await Task.Run(() => new Bitmap(_s.People.PathFor(key)));
            }
        }
        catch (Exception e) when (e is HttpRequestException or TmdbException or TaskCanceledException or IOException)
        {
            Biography = $"Can't reach TMDB: {e.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>By name: the most known person with exactly that name, preferring the right trade.</summary>
    private async Task<string?> FindAsync(TmdbClient client)
    {
        var results = await client.SearchPersonAsync(Name, CancellationToken.None);
        var same = results.Where(r => string.Equals(r.Name, Name, StringComparison.OrdinalIgnoreCase)).ToList();
        var trade = _director ? "Directing" : "Acting";
        return (same.Where(r => r.KnownFor == trade).MaxBy(r => r.Popularity) ?? same.MaxBy(r => r.Popularity))?.Id;
    }

    private static string? BuildFacts(TmdbPerson p)
    {
        var parts = new List<string>();
        if (p.KnownFor is { } k) parts.Add(k);
        if (DateTime.TryParse(p.Birthday, CultureInfo.InvariantCulture, DateTimeStyles.None, out var born))
        {
            var end = DateTime.TryParse(p.Deathday, CultureInfo.InvariantCulture, DateTimeStyles.None, out var died) ? died : DateTime.Today;
            var age = end.Year - born.Year - (end.DayOfYear < born.DayOfYear ? 1 : 0);
            parts.Add(p.Deathday is null
                ? $"Born {born.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)} ({age})"
                : $"{born.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)} – {died.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)} ({age})");
        }
        if (p.PlaceOfBirth is { } place) parts.Add(place);
        return parts.Count == 0 ? null : string.Join("  ·  ", parts);
    }

    [RelayCommand]
    private void OpenTitle(PersonTitle title) => _owner.OpenTitle(title.Card);

    [RelayCommand]
    private void OpenTmdb()
    {
        if (TmdbUrl is not null)
            Launcher.OpenUrl(TmdbUrl);
    }

    [RelayCommand]
    private void Back() => _owner.ClosePerson();

    public void Dispose()
    {
        _disposed = true;
        Photo?.Dispose();
    }
}
