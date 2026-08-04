using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

/// <summary>歌单搜索结果。字段来自 /search?type=1000 返回的 result.playlists。</summary>
public sealed class Playlist
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("coverImgUrl")]
    public string? CoverImgUrl { get; init; }

    /// <summary>推荐歌单接口（/personalized 等）封面字段是 picUrl。</summary>
    [JsonPropertyName("picUrl")]
    public string? PicUrl { get; init; }

    [JsonPropertyName("trackCount")]
    public int TrackCount { get; init; }

    /// <summary>首页个性化推荐（/homepage/block/page）只有播放量，没有 trackCount。</summary>
    [JsonPropertyName("playCount")]
    public long PlayCount { get; init; }

    /// <summary>特殊歌单标记：5 = 我喜欢的音乐。</summary>
    [JsonPropertyName("specialType")]
    public int SpecialType { get; init; }

    [JsonPropertyName("creator")]
    public PlaylistCreator? Creator { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"歌单 {Id}" : Name;

    public string CreatorName => Creator?.Nickname ?? "未知用户";

    /// <summary>副标题：有 trackCount 显示「N 首」，否则显示播放量（个性化推荐无 trackCount）。</summary>
    public string TrackCountText => TrackCount > 0
        ? $"{TrackCount} 首"
        : (PlayCount > 0 ? FormatPlayCount(PlayCount) : string.Empty);

    private static string FormatPlayCount(long count) => count switch
    {
        >= 100000000 => $"{count / 100000000.0:0.#}亿",
        >= 10000 => $"{count / 10000.0:0.#}万",
        _ => count.ToString(),
    };

    /// <summary>封面：搜索/用户歌单用 coverImgUrl，推荐歌单用 picUrl，取非空者。</summary>
    public string CoverUrl => !string.IsNullOrWhiteSpace(CoverImgUrl) ? CoverImgUrl : PicUrl ?? string.Empty;
}

public sealed class PlaylistCreator
{
    [JsonPropertyName("userId")]
    public long UserId { get; init; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; init; }
}
