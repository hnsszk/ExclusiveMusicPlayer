using System.IO;
using System.Text.Json;
using ExclusiveMusicPlayer.Models;

namespace ExclusiveMusicPlayer.Services;

/// <summary>
/// 登录会话持久化：把二维码登录拿到的 MUSIC_U cookie 与用户昵称存到
/// %APPDATA%\ExclusiveMusicPlayer\session.json。服务端（NeteaseMusic-API）
/// 不保存用户登录态，MUSIC_U 必须由客户端保管，之后每次请求带上。
/// </summary>
public sealed class LoginSession
{
    private static readonly string SessionDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExclusiveMusicPlayer");

    /// <summary>改名前（NeteaseExclusivePlayer）的旧会话目录。升级时若新目录无数据，从旧目录迁移登录态/设置。</summary>
    private static readonly string LegacySessionDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NeteaseExclusivePlayer");

    private static readonly string SessionFilePath = Path.Combine(SessionDirectory, "session.json");

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>保存的登录凭证（MUSIC_U=xxx 形式的 cookie）。为空表示未登录。</summary>
    public string Cookie { get; private set; } = string.Empty;

    public string Nickname { get; private set; } = string.Empty;

    /// <summary>登录用户 id（用于 /likelist、/user/playlist 等接口）。</summary>
    public long UserId { get; private set; }

    /// <summary>用户偏好的音质 key（仅记录用户手动选择的，自动降级不计入）。</summary>
    public string PreferredQuality { get; private set; } = "exhigh";

    /// <summary>用户偏好的播放模式（Sequential/Shuffle/RepeatOne，仅记录用户手动选择的）。</summary>
    public string PlaybackMode { get; private set; } = "Sequential";

    /// <summary>
    /// 网易云 API 服务地址。默认本地 NeteaseMusic-API（http://localhost:3000），
    /// 可在设置页改为云端部署地址（如 Vercel）或自建服务器。
    /// </summary>
    public string ApiBaseUrl { get; private set; } = "http://localhost:3000";

    /// <summary>
    /// 是否在播放器启动时自动拉起本地 API（含 3000 被占时自动换端口）。
    /// 默认开启；发布后若端口不可用或用户想手动管理 API，可在设置页关闭。
    /// </summary>
    public bool AutoStartLocalApi { get; private set; } = true;

    /// <summary>
    /// 音频输出模式（WASAPI Exclusive / Shared）。
    /// "exclusive" 固定启用独占模式；"shared" 切换到 Windows 共享混音模式。
    /// 只记录用户手动选择的，启动时据此恢复。
    /// </summary>
    public string OutputMode { get; private set; } = "exclusive";

    /// <summary>
    /// 用户选择的输出设备 id（MMDeviceEnumerator 匹配用）。空 = 跟随系统默认输出设备。
    /// 只记录用户手动选择的，启动时据此恢复。
    /// </summary>
    public string OutputDeviceId { get; private set; } = string.Empty;

    /// <summary>已保存有效的登录凭证。</summary>
    public bool HasSession => !string.IsNullOrWhiteSpace(Cookie);

    /// <summary>登录成功后保存会话（含用户 id 与昵称）。</summary>
    public void Save(string cookie, long userId, string nickname)
    {
        Cookie = cookie;
        UserId = userId;
        Nickname = nickname;
        Persist();
    }

    /// <summary>更新音质偏好并持久化。</summary>
    public void SavePreferredQuality(string qualityKey)
    {
        PreferredQuality = qualityKey;
        Persist();
    }

    /// <summary>更新播放模式偏好并持久化。</summary>
    public void SavePlaybackMode(string mode)
    {
        PlaybackMode = mode;
        Persist();
    }

    /// <summary>更新 API 服务地址并持久化。</summary>
    public void SaveApiBaseUrl(string baseUrl)
    {
        ApiBaseUrl = baseUrl;
        Persist();
    }

    /// <summary>更新「自动启动本地 API」开关并持久化。</summary>
    public void SaveAutoStartLocalApi(bool enabled)
    {
        AutoStartLocalApi = enabled;
        Persist();
    }

    /// <summary>更新音频输出模式（exclusive/shared）并持久化。</summary>
    public void SaveOutputMode(string mode)
    {
        OutputMode = mode;
        Persist();
    }

    /// <summary>更新输出设备 id 并持久化。传空字符串 = 跟随系统默认输出设备。</summary>
    public void SaveOutputDevice(string deviceId)
    {
        OutputDeviceId = deviceId ?? string.Empty;
        Persist();
    }

    /// <summary>登出，清除本机会话。</summary>
    public void Clear()
    {
        Cookie = string.Empty;
        UserId = 0;
        Nickname = string.Empty;
        Persist();
    }

    /// <summary>从磁盘恢复上次的登录会话。优先读新路径；新路径无数据且旧路径存在时，从旧路径迁移。</summary>
    public void Load()
    {
        try
        {
            var path = SessionFilePath;
            var legacyPath = Path.Combine(LegacySessionDirectory, "session.json");
            if (!File.Exists(path) && File.Exists(legacyPath))
            {
                // 改名升级：首次用新路径启动，把旧会话迁过来（登录态/设置不丢）。
                path = legacyPath;
            }

            if (!File.Exists(path))
            {
                return;
            }

            var session = JsonSerializer.Deserialize<SessionRecord>(
                File.ReadAllText(path),
                _jsonOptions);

            if (session != null)
            {
                Cookie = session.Cookie ?? string.Empty;
                UserId = session.UserId ?? 0;
                Nickname = session.Nickname ?? string.Empty;
                PreferredQuality = session.PreferredQuality ?? "exhigh";
                PlaybackMode = session.PlaybackMode ?? "Sequential";
                // 旧版本会话文件没有 ApiBaseUrl 字段（null）→ 用默认本地地址。
                ApiBaseUrl = session.ApiBaseUrl ?? "http://localhost:3000";
                // 旧版本会话文件没有该字段（null）→ 默认开启本地 API 自动启动。
                AutoStartLocalApi = session.AutoStartLocalApi ?? true;
                // 旧版本会话文件没有输出偏好字段（null）→ 保持默认独占 + 默认输出设备。
                OutputMode = session.OutputMode ?? "exclusive";
                OutputDeviceId = session.OutputDeviceId ?? string.Empty;
            }

            // 从旧路径读出的 → 立即迁移到新路径，之后都走新目录（旧文件保留作为备份）。
            if (path != SessionFilePath)
            {
                Persist();
            }
        }
        catch (Exception)
        {
            // 会话文件损坏时当作未登录处理，不影响启动。
            Cookie = string.Empty;
            UserId = 0;
            Nickname = string.Empty;
            PreferredQuality = "exhigh";
            PlaybackMode = "Sequential";
            AutoStartLocalApi = true;
            OutputMode = "exclusive";
            OutputDeviceId = string.Empty;
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(SessionDirectory);
            var record = new SessionRecord
            {
                Cookie = Cookie,
                UserId = UserId,
                Nickname = Nickname,
                PreferredQuality = PreferredQuality,
                PlaybackMode = PlaybackMode,
                ApiBaseUrl = ApiBaseUrl,
                AutoStartLocalApi = AutoStartLocalApi,
                OutputMode = OutputMode,
                OutputDeviceId = OutputDeviceId,
            };
            File.WriteAllText(SessionFilePath, JsonSerializer.Serialize(record, _jsonOptions));
        }
        catch (Exception)
        {
            // 保存失败不阻断主流程，最多丢失登录态。
        }
    }

    private sealed class SessionRecord
    {
        public string? Cookie { get; init; }

        public long? UserId { get; init; }

        public string? Nickname { get; init; }

        public string? PreferredQuality { get; init; }

        public string? PlaybackMode { get; init; }

        public string? ApiBaseUrl { get; init; }

        public bool? AutoStartLocalApi { get; init; }

        public string? OutputMode { get; init; }

        public string? OutputDeviceId { get; init; }
    }
}
