using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

/// <summary>歌单详情响应。对应 /playlist/detail 的返回（含完整封面等元信息）。</summary>
public sealed class PlaylistDetailResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("playlist")]
    public Playlist? Playlist { get; init; }
}
