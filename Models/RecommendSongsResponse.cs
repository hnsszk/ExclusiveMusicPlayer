using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

/// <summary>每日推荐歌曲响应。对应 /recommend/songs 的返回。</summary>
public sealed class RecommendSongsResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("data")]
    public RecommendSongsData? Data { get; init; }
}

public sealed class RecommendSongsData
{
    [JsonPropertyName("dailySongs")]
    public List<Song>? DailySongs { get; init; }
}
