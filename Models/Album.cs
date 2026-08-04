using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

/// <summary>专辑搜索结果。字段来自 /search?type=10 返回的 result.albums。</summary>
public sealed class Album
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("picUrl")]
    public string? CoverUrl { get; init; }

    [JsonPropertyName("size")]
    public int TrackCount { get; init; }

    [JsonPropertyName("artist")]
    public AlbumArtist? Artist { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"专辑 {Id}" : Name;

    public string ArtistName => Artist?.Name ?? "未知歌手";

    public string TrackCountText => $"{TrackCount} 首";
}

public sealed class AlbumArtist
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}
