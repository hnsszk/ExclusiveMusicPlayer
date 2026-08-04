using System.Text.Json.Serialization;

namespace ExclusiveMusicPlayer.Models;

/// <summary>
/// 首页发现页响应（/homepage/block/page）。
/// 从中取 HOMEPAGE_BLOCK_PLAYLIST_RCMD 块获得个性化推荐歌单，
/// 比 /personalized 更贴合用户口味，且 refresh=true 时换一批。
/// </summary>
public sealed class HomepageBlockPageResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("data")]
    public HomepageBlockData? Data { get; init; }
}

public sealed class HomepageBlockData
{
    [JsonPropertyName("blocks")]
    public List<HomepageBlock>? Blocks { get; init; }
}

public sealed class HomepageBlock
{
    [JsonPropertyName("blockCode")]
    public string? BlockCode { get; init; }

    /// <summary>块内卡片列表。每个 creative 的 resources 里含歌单数据。</summary>
    [JsonPropertyName("creatives")]
    public List<HomepageCreative>? Creatives { get; init; }
}

public sealed class HomepageCreative
{
    [JsonPropertyName("resources")]
    public List<HomepageResource>? Resources { get; init; }
}

public sealed class HomepageResource
{
    /// <summary>歌单 id（resourceType=list 时）。</summary>
    [JsonPropertyName("resourceId")]
    public long ResourceId { get; init; }

    /// <summary>封面等 UI 元信息。</summary>
    [JsonPropertyName("uiElement")]
    public HomepageUiElement? UiElement { get; init; }

    /// <summary>歌单播放量等扩展信息。</summary>
    [JsonPropertyName("resourceExtInfo")]
    public HomepageExtInfo? ResourceExtInfo { get; init; }
}

public sealed class HomepageUiElement
{
    [JsonPropertyName("mainTitle")]
    public HomepageTitle? MainTitle { get; init; }

    [JsonPropertyName("image")]
    public HomepageImage? Image { get; init; }
}

public sealed class HomepageTitle
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

public sealed class HomepageImage
{
    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; init; }
}

public sealed class HomepageExtInfo
{
    [JsonPropertyName("playCount")]
    public long PlayCount { get; init; }
}
