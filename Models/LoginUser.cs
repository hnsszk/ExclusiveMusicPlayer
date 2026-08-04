using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

/// <summary>登录用户的昵称头像信息。</summary>
public sealed class LoginUser
{
    [JsonPropertyName("userId")]
    public long UserId { get; init; }

    [JsonPropertyName("nickname")]
    public string Nickname { get; init; } = string.Empty;

    [JsonPropertyName("avatarUrl")]
    public string AvatarUrl { get; init; } = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? "网易云用户" : Nickname;
}

/// <summary>登录状态响应。对应 /login/status 的返回，data 里是账号信息。</summary>
public sealed class LoginStatusResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("data")]
    public LoginStatusData? Data { get; init; }
}

public sealed class LoginStatusData
{
    [JsonPropertyName("code")]
    public int? InnerCode { get; init; }

    [JsonPropertyName("account")]
    public object? Account { get; init; }

    [JsonPropertyName("profile")]
    public LoginUser? Profile { get; init; }
}
