using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

/// <summary>推荐歌单响应。对应 /personalized 的返回。</summary>
public sealed class PersonalizedResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("result")]
    public List<Playlist>? Result { get; init; }
}
