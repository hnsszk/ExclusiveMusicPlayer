using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

/// <summary>喜欢的音乐 id 列表响应。对应 /likelist 的返回。</summary>
public sealed class LikedSongsResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("ids")]
    public List<long>? Ids { get; init; }
}
