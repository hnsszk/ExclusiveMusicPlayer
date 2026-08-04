using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

/// <summary>歌单搜索响应。对应 /search?type=1000 的返回。</summary>
public sealed class PlaylistSearchResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("result")]
    public PlaylistSearchResult? Result { get; init; }
}

public sealed class PlaylistSearchResult
{
    [JsonPropertyName("playlists")]
    public List<Playlist>? Playlists { get; init; }
}
