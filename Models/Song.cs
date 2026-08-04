using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

public sealed class Song
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("ar")]
    public List<SongArtist> Artists { get; init; } = new();

    [JsonPropertyName("artists")]
    public List<SongArtist> SearchArtists { get; init; } = new();

    [JsonPropertyName("al")]
    public SongAlbum? Album { get; init; }

    [JsonPropertyName("album")]
    public SongAlbum? SearchAlbum { get; init; }

    [JsonPropertyName("dt")]
    public long DurationMilliseconds { get; init; }

    /// <summary>
    /// 搜索接口返回的时长字段（毫秒）；歌单接口用的是 dt。两个接口都映射，
    /// 取两者中非零的那个，避免搜索结果时长显示 00:00。
    /// </summary>
    [JsonPropertyName("duration")]
    public long DurationMillisecondsAlt { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"歌曲 {Id}" : Name;

    public string ArtistNames
    {
        get
        {
            var displayArtists = Artists.Count > 0 ? Artists : SearchArtists;
            return displayArtists.Count == 0
                ? "未知歌手"
                : string.Join(" / ", displayArtists.Select(a => string.IsNullOrWhiteSpace(a.Name) ? "未知歌手" : a.Name));
        }
    }

    public string DurationText
    {
        get
        {
            var ms = DurationMilliseconds > 0 ? DurationMilliseconds : DurationMillisecondsAlt;
            return TimeSpan.FromMilliseconds(ms).ToString(@"mm\:ss");
        }
    }

    public string CoverUrl => Album?.CoverUrl ?? SearchAlbum?.CoverUrl ?? string.Empty;
}

public sealed class SongArtist
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

public sealed class SongAlbum
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("picUrl")]
    public string? CoverUrl { get; set; }
}
