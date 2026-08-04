using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

public sealed class SearchResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("result")]
    public SearchResultData? Result { get; init; }
}

public sealed class SearchResultData
{
    [JsonPropertyName("songs")]
    public List<Song> Songs { get; init; } = new();

    [JsonPropertyName("songCount")]
    public int SongCount { get; init; }

    [JsonPropertyName("albums")]
    public List<Album> Albums { get; init; } = new();

    [JsonPropertyName("artists")]
    public List<Artist> Artists { get; init; } = new();
}
