using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

/// <summary>用户歌单列表响应。对应 /user/playlist 的返回。</summary>
public sealed class UserPlaylistResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("playlist")]
    public List<Playlist>? Playlist { get; init; }
}
