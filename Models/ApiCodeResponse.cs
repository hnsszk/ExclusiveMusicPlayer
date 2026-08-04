using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

/// <summary>
/// 只有 code 字段的通用响应。用于喜欢/收藏等无内容返回、只判断成败的操作接口
/// （/like、/playlist/tracks 等）。
/// </summary>
public sealed class ApiCodeResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }
}
