using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

public sealed class SongUrlResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("data")]
    public List<SongUrlInfo>? Data { get; init; }
}

public sealed class SongUrlInfo
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("br")]
    public int Bitrate { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("level")]
    public string? Level { get; init; }
}
