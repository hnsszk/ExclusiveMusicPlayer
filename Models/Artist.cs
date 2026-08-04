using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

/// <summary>歌手搜索结果。字段来自 /search?type=100 返回的 result.artists。</summary>
public sealed class Artist
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("picUrl")]
    public string? CoverUrl { get; init; }

    [JsonPropertyName("albumSize")]
    public int AlbumCount { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"歌手 {Id}" : Name;

    public string AlbumCountText => $"{AlbumCount} 张专辑";
}
