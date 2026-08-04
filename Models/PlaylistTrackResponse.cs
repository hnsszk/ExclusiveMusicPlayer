using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

public sealed class PlaylistTrackResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("songs")]
    public List<Song> Songs { get; init; } = new();
}
