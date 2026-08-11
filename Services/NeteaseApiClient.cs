using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExclusiveMusicPlayer.Models;

namespace ExclusiveMusicPlayer.Services;

public sealed class NeteaseApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _httpClient;

    /// <summary>底层 HttpClient（供调用方 Dispose 用）。BaseAddress 随 SetBaseAddress 更新。</summary>
    public HttpClient HttpClient => _httpClient;

    public NeteaseApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 登录凭证（MUSIC_U=xxx 形式的 cookie）。非空时所有请求都会带上，
    /// 服务端据此识别登录态。
    /// </summary>
    public string LoginCookie { get; set; } = string.Empty;

    /// <summary>更换 API 服务地址。切换后 BaseAddress 指向新服务，后续请求自动使用。</summary>
    public void SetBaseAddress(Uri baseAddress)
    {
        _httpClient.BaseAddress = baseAddress;
    }

    public async Task<IReadOnlyList<Song>> SearchSongsAsync(
        string keywords,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keywords))
        {
            throw new NeteaseApiException("请输入搜索关键词。");
        }

        var uri = $"/search?keywords={Uri.EscapeDataString(keywords)}&limit={limit}";
        var response = await GetAsync<SearchResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "搜索接口返回失败");
        return response.Result?.Songs ?? new List<Song>();
    }

    /// <summary>按关键词搜索歌单。type=1000 表示搜索歌单类型。</summary>
    public async Task<IReadOnlyList<Playlist>> SearchPlaylistsAsync(
        string keywords,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keywords))
        {
            throw new NeteaseApiException("请输入搜索关键词。");
        }

        var uri = $"/search?keywords={Uri.EscapeDataString(keywords)}&type=1000&limit={limit}";
        var response = await GetAsync<PlaylistSearchResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "歌单搜索接口返回失败");
        return response.Result?.Playlists ?? new List<Playlist>();
    }

    /// <summary>按关键词搜索专辑。type=10。</summary>
    public async Task<IReadOnlyList<Album>> SearchAlbumsAsync(
        string keywords,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keywords))
        {
            throw new NeteaseApiException("请输入搜索关键词。");
        }

        var uri = $"/search?keywords={Uri.EscapeDataString(keywords)}&type=10&limit={limit}";
        var response = await GetAsync<SearchResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "专辑搜索接口返回失败");
        return response.Result?.Albums ?? new List<Album>();
    }

    /// <summary>按关键词搜索歌手。type=100。</summary>
    public async Task<IReadOnlyList<Artist>> SearchArtistsAsync(
        string keywords,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keywords))
        {
            throw new NeteaseApiException("请输入搜索关键词。");
        }

        var uri = $"/search?keywords={Uri.EscapeDataString(keywords)}&type=100&limit={limit}";
        var response = await GetAsync<SearchResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "歌手搜索接口返回失败");
        return response.Result?.Artists ?? new List<Artist>();
    }

    /// <summary>专辑内歌曲。返回的歌曲自带完整封面，无需 /song/detail 补全。</summary>
    public async Task<IReadOnlyList<Song>> GetAlbumTracksAsync(
        long albumId,
        CancellationToken cancellationToken = default)
    {
        var uri = $"/album?id={albumId}";
        var response = await GetAsync<PlaylistTrackResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "专辑接口返回失败");
        return response.Songs ?? new List<Song>();
    }

    /// <summary>歌手热门 50 首。返回的歌曲自带完整封面。</summary>
    public async Task<IReadOnlyList<Song>> GetArtistTopSongsAsync(
        long artistId,
        CancellationToken cancellationToken = default)
    {
        var uri = $"/artist/top/song?id={artistId}";
        var response = await GetAsync<PlaylistTrackResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "歌手热门歌曲接口返回失败");
        return response.Songs ?? new List<Song>();
    }

    public async Task<IReadOnlyList<Song>> GetPlaylistTracksAsync(
        long playlistId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var uri = $"/playlist/track/all?id={playlistId}&limit={limit}";
        var response = await GetAsync<PlaylistTrackResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "歌单接口返回失败");
        return response.Songs ?? new List<Song>();
    }

    /// <summary>
    /// 获取单个歌单的详情（含完整封面）。用于首页日推卡片等只需元信息的场景。
    /// 歌单接口返回完整封面字段 coverImgUrl，避免搜索结果里 coverImgUrl 为空的封面丢失。
    /// </summary>
    public async Task<Playlist> GetPlaylistAsync(
        long playlistId,
        CancellationToken cancellationToken = default)
    {
        var uri = $"/playlist/detail?id={playlistId}";
        var response = await GetAsync<PlaylistDetailResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "歌单详情接口返回失败");
        return response.Playlist ?? throw new NeteaseApiException("歌单详情接口未返回数据。");
    }

    public async Task<IReadOnlyList<Song>> GetSongsDetailAsync(
        IEnumerable<long> songIds,
        CancellationToken cancellationToken = default)
    {
        var ids = songIds.Distinct().Take(100).ToArray();
        if (ids.Length == 0)
        {
            return new List<Song>();
        }

        var uri = $"/song/detail?ids={string.Join(',', ids)}";
        var response = await GetAsync<PlaylistTrackResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "歌曲详情接口返回失败");
        return response.Songs ?? new List<Song>();
    }

    /// <summary>
    /// 获取用户喜欢的音乐（顺序与网易云 App 一致）。
    /// 方法：先找「我喜欢的音乐」特殊歌单（specialType=5），再用 /playlist/track/all
    /// 拉取，返回顺序即 App 的收藏时间倒序。实测 /likelist 的 id 顺序并非收藏时间倒序。
    /// </summary>
    public async Task<IReadOnlyList<Song>> GetLikedSongsAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var playlistId = await GetLikedPlaylistIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (playlistId <= 0)
        {
            return new List<Song>();
        }

        // limit 传大值一次拉全量，保持 App 顺序。
        return await GetPlaylistTracksAsync(playlistId, 1000, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>找到「我喜欢的音乐」特殊歌单的 id（specialType=5）。找不到返回 0。</summary>
    public async Task<long> GetLikedPlaylistIdAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var uri = $"/user/playlist?uid={userId}&limit=100";
        var response = await GetAsync<UserPlaylistResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "用户歌单接口返回失败");

        return response.Playlist?.FirstOrDefault(p => p.SpecialType == 5)?.Id ?? 0;
    }

    /// <summary>获取用户的全部歌单（含创建和收藏，排除特殊歌单如「我喜欢的音乐」）。</summary>
    public async Task<IReadOnlyList<Playlist>> GetUserPlaylistsAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var uri = $"/user/playlist?uid={userId}&limit=100";
        var response = await GetAsync<UserPlaylistResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "用户歌单接口返回失败");

        // specialType=0 才是普通歌单；5=我喜欢的音乐等特殊歌单不列入。
        return response.Playlist?.Where(p => p.SpecialType == 0).ToList() ?? new List<Playlist>();
    }

    /// <summary>获取每日推荐歌曲（需登录）。返回完整 Song 列表，封面在 al.picUrl。</summary>
    public async Task<IReadOnlyList<Song>> GetDailySongsAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<RecommendSongsResponse>("/recommend/songs", cancellationToken).ConfigureAwait(false);
        if (response.Code == 301)
        {
            throw new NeteaseApiException("今日推荐需要登录后才能查看。");
        }

        EnsureCode(response.Code, "今日推荐接口返回失败");
        return response.Data?.DailySongs ?? new List<Song>();
    }

    /// <summary>获取推荐歌单（游客可用）。封面字段是 picUrl。</summary>
    public async Task<IReadOnlyList<Playlist>> GetRecommendedPlaylistsAsync(
        int limit = 12,
        CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<PersonalizedResponse>($"/personalized?limit={limit}", cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "推荐歌单接口返回失败");
        return response.Result ?? new List<Playlist>();
    }

    /// <summary>
    /// 获取首页发现页的个性化推荐歌单（/homepage/block/page 的 PLAYLIST_RCMD 块）。
    /// 比 /personalized 更贴合用户口味（已登录时）；refresh=true 时换一批新的。
    /// 返回的 Playlist 只有 id/name/封面/播放量（无 trackCount/creator），
    /// 副标题走 TrackCountText 的播放量显示。
    /// </summary>
    public async Task<IReadOnlyList<Playlist>> GetHomepageRecommendedPlaylistsAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var uri = $"/homepage/block/page?refresh={(refresh ? "true" : "false")}";
        var response = await GetAsync<HomepageBlockPageResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "首页推荐接口返回失败");

        var block = response.Data?.Blocks?.FirstOrDefault(b => b.BlockCode == "HOMEPAGE_BLOCK_PLAYLIST_RCMD");
        if (block?.Creatives is null)
        {
            return new List<Playlist>();
        }

        var playlists = new List<Playlist>();
        foreach (var creative in block.Creatives)
        {
            var res = creative.Resources?.FirstOrDefault();
            if (res is null || res.ResourceId <= 0)
            {
                continue;
            }

            playlists.Add(new Playlist
            {
                Id = res.ResourceId,
                Name = res.UiElement?.MainTitle?.Title ?? string.Empty,
                PicUrl = res.UiElement?.Image?.ImageUrl,
                PlayCount = res.ResourceExtInfo?.PlayCount ?? 0,
            });
        }

        return playlists;
    }

    /// <summary>只获取喜欢的音乐 id 列表（用于增量刷新）。顺序即收藏顺序。</summary>
    public async Task<IReadOnlyList<long>> GetLikedSongIdsAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var uri = $"/likelist?uid={userId}";
        var likeResponse = await GetAsync<LikedSongsResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(likeResponse.Code, "喜欢的音乐接口返回失败");
        return likeResponse.Ids ?? new List<long>();
    }

    /// <summary>
    /// 喜欢/取消喜欢一首歌曲（写「我喜欢的音乐」）。
    /// like=true 加入喜欢列表，false 取消喜欢。
    /// </summary>
    public async Task LikeSongAsync(
        long songId,
        bool like,
        CancellationToken cancellationToken = default)
    {
        var uri = $"/like?id={songId}&like={(like ? "true" : "false")}";
        var response = await GetAsync<ApiCodeResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, like ? "喜欢歌曲失败" : "取消喜欢失败");
    }

    /// <summary>
    /// 把歌曲加入指定歌单（收藏）。op=add；需要登录。
    /// 歌单 id 和歌曲 id 必须真实存在，否则服务端可能报 502。
    /// </summary>
    public async Task AddSongToPlaylistAsync(
        long playlistId,
        long songId,
        CancellationToken cancellationToken = default)
    {
        var uri = $"/playlist/tracks?op=add&pid={playlistId}&tracks={songId}";
        var response = await GetAsync<ApiCodeResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "收藏到歌单失败");
    }

    /// <summary>
    /// 从指定歌单删除歌曲。op=del；需要登录。可用于「我喜欢的音乐」和自建歌单。
    /// </summary>
    public async Task RemoveSongFromPlaylistAsync(
        long playlistId,
        long songId,
        CancellationToken cancellationToken = default)
    {
        var uri = $"/playlist/tracks?op=del&pid={playlistId}&tracks={songId}";
        var response = await GetAsync<ApiCodeResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "从歌单删除歌曲失败");
    }

    /// <summary>
    /// 收藏/取消收藏歌单（订阅/退订）。subscribe=true 收藏（t=1），false 取消收藏（t=2）。
    /// 需要登录；成功后歌单出现在「我的歌单」的收藏分组。
    /// </summary>
    public async Task SubscribePlaylistAsync(
        long playlistId,
        bool subscribe,
        CancellationToken cancellationToken = default)
    {
        var uri = $"/playlist/subscribe?t={(subscribe ? 1 : 2)}&id={playlistId}";
        var response = await GetAsync<ApiCodeResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, subscribe ? "收藏歌单失败" : "取消收藏歌单失败");
    }

    /// <summary>
    /// 获取歌曲播放地址。使用官方源（unblock=false）+ 已保存的登录 cookie，
    /// VIP 歌曲在登录后能拿到完整无损音质；未登录自动降级为标准音质/试听。
    /// 返回的 type 字段用于选择解码器（mp3/flac）。
    /// </summary>
    public async Task<SongUrlInfo> GetPlayableSongUrlAsync(
        long songId,
        string level,
        CancellationToken cancellationToken = default)
    {
        var uri = $"/song/url/v1?id={songId}&level={Uri.EscapeDataString(level)}&unblock=false";
        var response = await GetAsync<SongUrlResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "歌曲 URL 接口返回失败");

        var songUrl = response.Data?.FirstOrDefault(item => item.Id == songId) ?? response.Data?.FirstOrDefault();
        if (songUrl == null || string.IsNullOrWhiteSpace(songUrl.Url))
        {
            throw new NeteaseApiException("该歌曲当前不可播放，可能受版权或 VIP 限制。");
        }

        return songUrl;
    }

    /// <summary>生成二维码登录的 key。</summary>
    public async Task<string> GetLoginQrKeyAsync(CancellationToken cancellationToken = default)
    {
        // 加时间戳绕过服务的 2 分钟缓存中间件，避免轮询拿到旧的 key。
        var uri = $"/login/qr/key?timestamp={TimeStamp()}";
        var response = await GetAsync<LoginQrKeyResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "获取登录二维码失败");

        if (string.IsNullOrWhiteSpace(response.Data?.UniKey))
        {
            throw new NeteaseApiException("获取登录二维码失败，服务未返回 key。");
        }

        return response.Data.UniKey;
    }

    /// <summary>用 key 生成二维码，返回 data:image/png;base64 可直接用于 Image 显示。</summary>
    public async Task<string> GetLoginQrImageAsync(string key, CancellationToken cancellationToken = default)
    {
        var uri = $"/login/qr/create?key={Uri.EscapeDataString(key)}&qrimg=true&timestamp={TimeStamp()}";
        var response = await GetAsync<LoginQrCreateResponse>(uri, cancellationToken).ConfigureAwait(false);
        EnsureCode(response.Code, "生成登录二维码失败");

        if (string.IsNullOrWhiteSpace(response.Data?.QrImg))
        {
            throw new NeteaseApiException("生成登录二维码失败，服务未返回图片。");
        }

        return response.Data.QrImg;
    }

    /// <summary>
    /// 轮询扫码状态。返回网易的 code：800=过期、801=等待扫码、802=待确认、803=授权成功。
    /// 803 时返回的 cookie 为登录凭证（MUSIC_U）。
    /// </summary>
    public async Task<LoginQrCheckResponse> CheckLoginQrAsync(string key, CancellationToken cancellationToken = default)
    {
        var uri = $"/login/qr/check?key={Uri.EscapeDataString(key)}&timestamp={TimeStamp()}";
        var response = await GetAsync<LoginQrCheckResponse>(uri, cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <summary>查询当前登录状态（完整响应，含用户昵称等信息）。</summary>
    public async Task<LoginStatusResponse> GetLoginStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<LoginStatusResponse>("/login/status", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 查询当前登录状态。该接口的 data.code 无论是否登录都返回 200，
    /// 判断登录态的正确依据是 data.profile 是否存在（未登录时为 null）。
    /// </summary>
    public async Task<bool> IsLoggedInAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetLoginStatusAsync(cancellationToken).ConfigureAwait(false);
        return response.Data?.Profile is not null;
    }

    /// <summary>退出登录。</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<LoginStatusResponse>("/logout", cancellationToken).ConfigureAwait(false);
        // 登出成功或已在登出状态都视为成功。
        if (response.Code != 200 && response.Code != 301)
        {
            throw new NeteaseApiException($"登出失败，服务返回 code={response.Code}。");
        }
    }

    /// <summary>
    /// 检查当前配置的服务器是否是可用的网易云 API 服务。
    /// 信号：/login/status 返回合法 JSON 且 code==200。
    /// 注意：/login/status 的 code 在 data 里（{"data":{"code":200,...}}），顶层通常不带 code，
    /// 因此需同时检查 response.Code（顶层）与 response.Data.InnerCode（data.code）两个位置。
    /// 未登录时 profile 为 null 但 data.code 仍为 200，所以不依赖登录态。
    /// 任何异常、非 JSON 或 code!=200 都视为不可用。
    /// </summary>
    public async Task<bool> IsServerReachableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await GetAsync<LoginStatusResponse>("/login/status", cancellationToken)
                .ConfigureAwait(false);
            return response.Code == 200 || response.Data?.InnerCode == 200;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 探测任意地址是否为可用的网易云 API 服务（独立临时 HttpClient，不影响当前工作客户端）。
    /// 用于设置页「测试连接/保存」校验新地址。
    /// </summary>
    public static async Task<bool> ProbeServerAsync(
        Uri baseAddress,
        string? cookie = null,
        CancellationToken cancellationToken = default)
    {
        using var probe = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(10),
        };
        var client = new NeteaseApiClient(probe)
        {
            LoginCookie = cookie ?? string.Empty,
        };
        return await client.IsServerReachableAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long TimeStamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private async Task<T> GetAsync<T>(string relativeUri, CancellationToken cancellationToken)
    {
        try
        {
            // 登录后所有请求附带 cookie（MUSIC_U），服务端据此识别登录态。
            var uri = relativeUri;
            if (!string.IsNullOrEmpty(LoginCookie))
            {
                uri += (uri.Contains('?') ? "&" : "?") + "cookie=" + Uri.EscapeDataString(LoginCookie);
            }

            return await _httpClient.GetFromJsonAsync<T>(uri, JsonOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new NeteaseApiException("接口返回了空响应。");
        }
        catch (HttpRequestException ex)
        {
            throw new NeteaseApiException(
                "无法连接网易云 API 服务，请确认 API 服务已启动，或到「设置」中修改 API 地址。", ex);
        }
    }

    private static void EnsureCode(int code, string message)
    {
        if (code != 200)
        {
            throw new NeteaseApiException($"{message}，服务返回 code={code}。");
        }
    }
}
