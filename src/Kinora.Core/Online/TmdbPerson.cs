using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kinora.Core.Online;

/// <summary>What a person page shows from TMDB.</summary>
public sealed class TmdbPerson
{
    public string Id { get; set; } = "";
    public string Language { get; set; } = "en-US";
    public string Name { get; set; } = "";
    public string? Biography { get; set; }
    public string? Birthday { get; set; }
    public string? Deathday { get; set; }
    public string? PlaceOfBirth { get; set; }
    public string? KnownFor { get; set; }
    public string? ProfilePath { get; set; }
    public string? ImdbId { get; set; }

    [JsonIgnore]
    public string Url => $"https://www.themoviedb.org/person/{Id}";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static TmdbPerson? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<TmdbPerson>(json, Json); }
        catch (JsonException) { return null; }
    }
}

public sealed record TmdbPersonResult(string Id, string Name, string? KnownFor, double Popularity, string? ProfilePath);
