using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kinora.Core.Online;

/// <summary>
/// What Kinora keeps of a TMDB movie: only the fields it uses, so the cache stays small
/// (a few KB per movie instead of the 30-50 KB of the full answer).
/// </summary>
public sealed class TmdbMovie
{
    public string Id { get; set; } = "";
    public string? ImdbId { get; set; }
    public string Language { get; set; } = "en-US";
    public string Title { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public string? Overview { get; set; }
    public string? Tagline { get; set; }
    public string? ReleaseDate { get; set; }
    public int? Runtime { get; set; }
    public double? VoteAverage { get; set; }
    public string? Certification { get; set; }
    public string? Collection { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Studios { get; set; } = [];
    public List<string> Countries { get; set; } = [];
    public List<string> Directors { get; set; } = [];
    public List<TmdbCast> Cast { get; set; } = [];
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? LogoPath { get; set; }

    [JsonIgnore]
    public int? Year => ReleaseDate is { Length: >= 4 } d && int.TryParse(d[..4], out var y) ? y : null;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static TmdbMovie? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TmdbMovie>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record TmdbCast(string Name, string? Character);

public sealed record TmdbSearchResult(string Id, string Title, string? OriginalTitle, int? Year, string? Overview,
                                      string? PosterPath, double Popularity)
{
    public string Label => Year is { } y ? $"{Title} ({y})" : Title;
}
