using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core.Online;

namespace Kinora.ViewModels;

public partial class IdentifyResult(TmdbSearchResult result) : ObservableObject
{
    public TmdbSearchResult Result { get; } = result;
    public string Title => Result.Title;
    public string? Details => string.Join("  ·  ", new[]
    {
        Result.Year?.ToString(CultureInfo.InvariantCulture),
        Result.OriginalTitle is { } o && o != Result.Title ? o : null,
    }.Where(s => s is not null));
    public string? Overview => Result.Overview;

    [ObservableProperty]
    public partial Bitmap? Poster { get; set; }
}

/// <summary>Search TMDB by hand and say which movie a file is.</summary>
public partial class IdentifyViewModel : ViewModelBase, IDisposable
{
    private readonly TmdbClient _client;
    private CancellationTokenSource? _search;

    public IdentifyViewModel(TmdbClient client, string fileName, string title, int? year)
    {
        _client = client;
        FileName = fileName;
        Query = title;
        YearText = year?.ToString(CultureInfo.InvariantCulture) ?? "";
        _ = SearchAsync();
    }

    public string FileName { get; }
    public ObservableCollection<IdentifyResult> Results { get; } = [];
    public string? ChosenId { get; private set; }
    public event Action? CloseRequested;

    [ObservableProperty]
    public partial string Query { get; set; }

    [ObservableProperty]
    public partial string YearText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseCommand))]
    public partial IdentifyResult? Selected { get; set; }

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    [RelayCommand]
    private Task Search() => SearchAsync();

    private async Task SearchAsync()
    {
        if (_search is not null)
            await _search.CancelAsync();
        _search = new CancellationTokenSource();
        var ct = _search.Token;
        var query = Query.Trim();
        if (query.Length == 0)
            return;
        int? year = int.TryParse(YearText.Trim(), out var y) && y > 1800 ? y : null;

        IsSearching = true;
        Message = null;
        Results.Clear();
        try
        {
            var found = await _client.SearchMovieAsync(query, year, ct);
            if (found.Count == 0 && year is not null)
                found = await _client.SearchMovieAsync(query, null, ct);
            foreach (var r in found.Take(20))
                Results.Add(new IdentifyResult(r));
            Message = Results.Count == 0 ? "Nothing found. Try another title, or leave the year empty." : null;
            Selected = Results.FirstOrDefault();
            foreach (var item in Results.ToList())
                _ = LoadPosterAsync(item, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is HttpRequestException or TmdbException)
        {
            Message = e.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    private static async Task LoadPosterAsync(IdentifyResult item, CancellationToken ct)
    {
        if (item.Result.PosterPath is not { } path)
            return;
        var bytes = await TmdbImages.DownloadAsync(path, "w154", ct);
        if (bytes is null || ct.IsCancellationRequested)
            return;
        try
        {
            using var stream = new MemoryStream(bytes);
            item.Poster = new Bitmap(stream);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
        }
    }

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void Choose()
    {
        ChosenId = Selected?.Result.Id;
        CloseRequested?.Invoke();
    }

    private bool CanChoose() => Selected is not null;

    public void Dispose()
    {
        _search?.Cancel();
        foreach (var r in Results)
            r.Poster?.Dispose();
    }
}
