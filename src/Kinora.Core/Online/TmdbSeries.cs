using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kinora.Core.Online;

/// <summary>What Kinora keeps of a TMDB show.</summary>
public sealed class TmdbSeries
{
    public string Id { get; set; } = "";
    public string? ImdbId { get; set; }
    public string? TvdbId { get; set; }
    public string Language { get; set; } = "en-US";
    public string Name { get; set; } = "";
    public string? OriginalName { get; set; }
    public string? Overview { get; set; }
    public string? FirstAirDate { get; set; }
    public string? LastAirDate { get; set; }
    public string? Status { get; set; }
    public double? VoteAverage { get; set; }
    public string? Certification { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Networks { get; set; } = [];
    public List<TmdbCast> Cast { get; set; } = [];
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? LogoPath { get; set; }

    /// <summary>Season number → poster path, from the show's own answer (no extra call).</summary>
    public Dictionary<int, string> SeasonPosters { get; set; } = [];

    [JsonIgnore]
    public int? Year => FirstAirDate is { Length: >= 4 } d && int.TryParse(d[..4], out var y) ? y : null;

    [JsonIgnore]
    public int? EndYear => Status is "Ended" or "Canceled" && LastAirDate is { Length: >= 4 } d && int.TryParse(d[..4], out var y) ? y : null;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static TmdbSeries? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<TmdbSeries>(json, Json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>One season of a TMDB show, with its episodes.</summary>
public sealed class TmdbSeason
{
    public int Number { get; set; }
    public string Language { get; set; } = "en-US";
    public string? PosterPath { get; set; }
    public List<TmdbEpisode> Episodes { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static TmdbSeason? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<TmdbSeason>(json, Json); }
        catch (JsonException) { return null; }
    }
}

public sealed record TmdbEpisode(int Number, string Name, string? Overview, string? AirDate, int? Runtime, double? VoteAverage, string? StillPath);
