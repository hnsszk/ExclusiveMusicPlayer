using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

/// <summary>二维码 key 响应。对应 /login/qr/key 的返回。</summary>
public sealed class LoginQrKeyResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("data")]
    public LoginQrKeyData? Data { get; init; }
}

public sealed class LoginQrKeyData
{
    [JsonPropertyName("unikey")]
    public string? UniKey { get; init; }
}

/// <summary>二维码生成响应。对应 /login/qr/create 的返回。</summary>
public sealed class LoginQrCreateResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("data")]
    public LoginQrCreateData? Data { get; init; }
}

public sealed class LoginQrCreateData
{
    [JsonPropertyName("qrurl")]
    public string? QrUrl { get; init; }

    [JsonPropertyName("qrimg")]
    public string? QrImg { get; init; }
}

/// <summary>扫码状态响应。对应 /login/qr/check 的返回。</summary>
public sealed class LoginQrCheckResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("cookie")]
    public string? Cookie { get; init; }
}
