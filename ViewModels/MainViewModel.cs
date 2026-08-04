using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Data;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ExclusiveMusicPlayer.Models;
using ExclusiveMusicPlayer.Services;

namespace ExclusiveMusicPlayer.ViewModels;

/// <summary>播放模式：顺序 / 随机 / 单曲循环。</summary>
public enum PlaybackMode
{
    Sequential,
    Shuffle,
    RepeatOne,
}

/// <summary>搜索类型：歌曲 / 歌单 / 专辑 / 歌手。</summary>
public enum SearchType
{
    Song,
    Playlist,
    Album,
    Artist,
}

/// <summary>音频输出模式：WASAPI 独占 / 共享（走 Windows 混音器）。</summary>
public enum OutputMode
{
    Exclusive,
    Shared,
}

/// <summary>搜索类型选项（Type 传接口，Display 显示名）。</summary>
public sealed record SearchTypeOption(SearchType Type, string Display);

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private const uint AudioClientDeviceInUse = 0x8889000A;

    /// <summary>
    /// 当前 API 客户端与其底层 HttpClient。
    /// 注意：HttpClient 一旦发送过请求就不能再改 BaseAddress（会抛
    /// "This instance has already started one or more requests..."）。
    /// 因此设置页切换 API 地址时【整体替换】这两个字段，而不是改旧实例的属性。
    /// </summary>
    private HttpClient _httpClient;
    private NeteaseApiClient _apiClient;
    private readonly AudioPlaybackService _playbackService;
    private readonly LoginSession _loginSession;
    private readonly LikedSongsCache _likedCache = new();
    /// <summary>我创建的歌单曲目缓存（按歌单 id 分键，与喜欢缓存完全独立）。</summary>
    private readonly PlaylistSongsCache _playlistCache = new();
    /// <summary>「我的歌单」列表元信息缓存（创建 + 收藏的缩略图/名称/歌曲数，不含内部歌曲）。</summary>
    private readonly MyPlaylistListCache _myPlaylistCache = new();
    /// <summary>
    /// 喜欢歌曲 id 的集合（在「我喜欢的音乐」歌单里的歌）。
    /// 用于底部栏红心图标状态与显示：当前播放歌曲 id 在此集合中 → 显示红心收藏后的图标。
    /// 只在「我喜欢的音乐」页与登录/登出时全量刷新。
    /// </summary>
    private readonly HashSet<long> _likedSongIds = new();
    /// <summary>防重入：红心/收藏操作请求中（图标闪烁等交互期间防止重复触发）。</summary>
    private bool _isLikeActionInFlight;
    private ICollectionView? _songsView;
    /// <summary>
    /// WPF UI 线程的 SynchronizationContext。API 内部用 ConfigureAwait(false)，
    /// continuation 会留在线程池；所有加载方法通过 FetchUiAsync/BackToUiAsync 回到此线程
    /// 再操作 ObservableCollection 等 UI 状态，否则 WPF 绑定会抛跨线程异常。
    /// </summary>
    private readonly SynchronizationContext? _uiSyncContext;

    private Song? _selectedSong;
    private string _searchKeyword = "陈奕迅";
    private string _homeSearchText = string.Empty;
    private string _likedSearchText = string.Empty;
    private string _currentSongText = "未在播放";
    private string _currentSongCoverUrl = string.Empty;
    /// <summary>当前播放歌曲的 id。喜欢列表/红心状态用（_currentIndex 会被列表替换重置为 -1）。</summary>
    private long _currentSongId;
    /// <summary>右键「收藏到歌单」的目标歌曲：收藏弹窗打开时把这首歌曲收藏进所选歌单。</summary>
    private long _pendingCollectSongId;
    /// <summary>右键「收藏到歌单」目标歌曲的显示名（弹窗标题用）。</summary>
    private string _pendingCollectSongText = string.Empty;
    /// <summary>当前歌曲列表所属的自建歌单 id（仅 ListContext.CreatedPlaylist 时有意义，删除歌曲用）。</summary>
    private long _currentPlaylistId;
    private string _statusText = "就绪";
    private bool _isBusy;
    private bool _isPlaying;
    private bool _isPaused;
    private int _currentIndex = -1;
    private double _positionRatio;
    private string _positionText = "00:00";
    private string _durationText = "00:00";
    private double _volume = 1.0;
    private bool _isSeeking;
    private TimeSpan _cachedDuration = TimeSpan.Zero;
    private bool _isPositionUpdateFromPlayback;
    private LoginViewModel? _loginState;
    private QualityOption _preferredQuality = new("exhigh", "极高");
    /// <summary>播放请求代次：切歌/下一首/上一首都会递增，旧请求返回后不覆盖 UI 状态。</summary>
    private int _playRequestId;

    // ---- 搜索状态 ----
    private SearchType _currentSearchType = SearchType.Song;
    private SearchTypeOption? _currentSearchTypeOption;
    /// <summary>搜索请求代次：切类型时旧搜索请求结果丢弃，防止旧结果污染当前类型列表。</summary>
    private int _searchRequestId;

    private PlaybackMode _playbackMode = PlaybackMode.Sequential;
    private readonly Random _random = new();
    /// <summary>
    /// 随机模式下的固定播放队列：播放队列（_playQueue）索引的随机排列（环）。
    /// 非 Shuffle 或未建时为 null（自然序）。进入随机时一次性洗牌，之后在队列内顺序导航，不再每首重新随机。
    /// </summary>
    private int[]? _playOrder;
    /// <summary>
    /// 固定的播放队列：播放动作（双击歌曲 / 悬停播放 / 播放全部）产生的一次性快照。
    /// Songs 只表示「当前展示的列表」，浏览/搜索/点开其他歌单都不会改动队列；
    /// 上一首 / 下一首 / 自然连播都只在 _playQueue 内进行（顺序/随机/单曲模式统一）。
    /// </summary>
    private List<Song> _playQueue = new();

    // ---- 首页数据 ----
    private IReadOnlyList<Song>? _dailySongs;
    private bool _recommendedLoaded;

    /// <summary>首页第一栏日推歌单（私人雷达/日系/欧美）的固定歌单 id 与显示名。</summary>
    private static readonly (long Id, string Title)[] DailyPlaylists =
    [
        (3136952023L, "私人雷达"),
        (2829896389L, "日系雷达"),
        (2829816518L, "欧美雷达"),
    ];

    /// <summary>音质选项（Key 传给接口，Display 显示名）。</summary>
    public sealed record QualityOption(string Key, string Display);

    public static IReadOnlyList<QualityOption> QualityOptions { get; } =
    [
        new("standard", "标准"),
        new("higher", "较高"),
        new("exhigh", "极高"),
        new("lossless", "无损"),
        new("hires", "Hi-Res"),
        new("jyeffect", "高清环绕声"),
        new("sky", "沉浸环绕声"),
        new("dolby", "杜比全景声"),
        new("jymaster", "超清母带"),
    ];

    /// <summary>
    /// 用户偏好的音质。只有用户手动选择时才更新并持久化；
    /// 某首歌不支持所选音质而自动降级时，不修改此偏好（降级只影响状态栏提示）。
    /// </summary>
    public QualityOption PreferredQuality
    {
        get => _preferredQuality;
        set
        {
            if (SetProperty(ref _preferredQuality, value))
            {
                // 记住用户手动选择的音质，下次启动沿用。
                _loginSession.SavePreferredQuality(value.Key);

                // 切换音质后，正在播放的歌曲用新音质重新加载。
                _ = ReloadWithNewQualityAsync();
            }
        }
    }

    /// <summary>当前音质 key（传给接口）。</summary>
    public string QualityLevel => PreferredQuality.Key;

    /// <summary>搜索类型选项（UI 类型选择器用）。</summary>
    public static IReadOnlyList<SearchTypeOption> SearchTypeOptions { get; } =
    [
        new(SearchType.Song, "歌曲"),
        new(SearchType.Playlist, "歌单"),
        new(SearchType.Album, "专辑"),
        new(SearchType.Artist, "歌手"),
    ];

    /// <summary>
    /// 切换搜索类型。reSearch=true 表示用户点击类型选择器（用当前关键词重新搜索）；
    /// reSearch=false 表示程序内切换（刚加载完内容，不重复搜索）。
    /// </summary>
    public void SwitchSearchType(SearchType type, bool reSearch)
    {
        if (type == _currentSearchType)
        {
            return;
        }

        _currentSearchType = type;
        _currentSearchTypeOption = SearchTypeOptions.First(o => o.Type == type);
        OnPropertyChanged(nameof(CurrentSearchType));
        OnPropertyChanged(nameof(CurrentSearchTypeOption));

        if (reSearch && !string.IsNullOrWhiteSpace(_searchKeyword))
        {
            _ = SearchAsync();
        }
    }

    /// <summary>播放模式（顺序/随机/单曲循环）。</summary>
    public PlaybackMode PlaybackMode
    {
        get => _playbackMode;
        set
        {
            if (SetProperty(ref _playbackMode, value))
            {
                // 切出随机模式时清空固定随机队列，保证 _playOrder 只在 Shuffle 模式下非 null。
                if (value != PlaybackMode.Shuffle)
                {
                    _playOrder = null;
                }

                OnPropertyChanged(nameof(PlaybackModeText));
                OnPropertyChanged(nameof(PlaybackModeIcon));
            }
        }
    }

    /// <summary>循环模式按钮文字：顺序 → 随机 → 单曲循环。</summary>
    public string PlaybackModeText => PlaybackMode switch
    {
        PlaybackMode.Shuffle => "随机",
        PlaybackMode.RepeatOne => "单曲循环",
        _ => "顺序",
    };

    /// <summary>循环模式按钮图标（Segoe MDL2）：顺序=循环、随机=随机、单曲循环=单曲循环。</summary>
    public string PlaybackModeIcon => PlaybackMode switch
    {
        PlaybackMode.Shuffle => "", // Shuffle
        PlaybackMode.RepeatOne => "", // RepeatOne
        _ => "", // RepeatAll
    };

    /// <summary>点击循环按钮：顺序 → 随机 → 单曲循环 → 顺序。</summary>
    public void CyclePlaybackMode()
    {
        PlaybackMode = PlaybackMode switch
        {
            PlaybackMode.Sequential => PlaybackMode.Shuffle,
            PlaybackMode.Shuffle => PlaybackMode.RepeatOne,
            _ => PlaybackMode.Sequential,
        };
        StatusText = $"播放模式：{PlaybackModeText}";

        // 记忆播放模式：把当前模式持久化到 session.json，下次启动时恢复。
        _loginSession.SavePlaybackMode(PlaybackMode.ToString());

        // 切到随机时，对当前播放队列一次性洗牌生成固定随机队列（需求 B）。
        // 正在播的歌锚到队列首位；未播放则纯洗牌。
        // 注意：切模式不是播放动作，播放队列为空（尚未播过）时不从展示列表新建队列。
        if (PlaybackMode == PlaybackMode.Shuffle && _playQueue.Count > 0)
        {
            RebuildPlayOrder(_currentIndex >= 0 ? _currentIndex : -1);
        }
    }

    /// <summary>API 客户端（供登录窗口等直接使用）。</summary>
    public NeteaseApiClient ApiClient => _apiClient;

    /// <summary>
    /// 创建指向指定 API 地址的 HttpClient。设置页保存时复用同一实例（改 BaseAddress），
    /// 不重建，避免引用外泄与重复 Dispose。
    /// </summary>
    private static HttpClient CreateHttpClient(string baseUrl) => new()
    {
        BaseAddress = new Uri(baseUrl),
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>设置页显示/编辑的 API 服务地址（启动时取自会话持久化值）。</summary>
    public string ApiBaseUrl
    {
        get => _apiBaseUrl;
        set
        {
            if (SetProperty(ref _apiBaseUrl, value))
            {
                OnPropertyChanged(nameof(ApiCanSave));
            }
        }
    }

    private string _apiBaseUrl = "http://localhost:3000";

    /// <summary>是否在播放器启动时自动拉起本地 API（含 3000 被占自动换端口）。默认开启。</summary>
    public bool AutoStartLocalApi
    {
        get => _autoStartLocalApi;
        set
        {
            if (SetProperty(ref _autoStartLocalApi, value))
            {
                _loginSession.SaveAutoStartLocalApi(value);
            }
        }
    }

    private bool _autoStartLocalApi = true;

    /// <summary>测试/保存进行中（两个按钮共享忙碌状态，避免并发探测）。</summary>
    public bool ApiBusy
    {
        get => _apiBusy;
        private set
        {
            if (SetProperty(ref _apiBusy, value))
            {
                OnPropertyChanged(nameof(ApiCanSave));
                OnPropertyChanged(nameof(ApiCanTest));
            }
        }
    }

    private bool _apiBusy;

    /// <summary>保存按钮是否可用（地址非空且未在处理中）。</summary>
    public bool ApiCanSave => !_apiBusy && !string.IsNullOrWhiteSpace(ApiBaseUrl);

    /// <summary>「测试连接」按钮是否可用（未在处理中）。</summary>
    public bool ApiCanTest => !_apiBusy;

    /// <summary>设置页 API 连接测试/保存的结果提示（成功/失败/空闲）。</summary>
    public string ApiConnectionStatus
    {
        get => _apiConnectionStatus;
        set
        {
            if (SetProperty(ref _apiConnectionStatus, value))
            {
                OnPropertyChanged(nameof(ApiStatusIsError));
            }
        }
    }

    private string _apiConnectionStatus = string.Empty;

    /// <summary>连接状态提示是否为错误（红色）。false = 成功绿或空闲灰。</summary>
    public bool ApiStatusIsError
    {
        get => _apiStatusIsError;
        private set => SetProperty(ref _apiStatusIsError, value);
    }

    private bool _apiStatusIsError;

    /// <summary>设置页卡片右上角显示当前生效的 API 地址（成功保存后更新）。</summary>
    public string ApiCurrentServerText
    {
        get => $"当前：{_httpClient.BaseAddress?.OriginalString ?? "未设置"}";
    }

    /// <summary>校验地址是否合法（http/https 绝对 URL），非法时在页面提示并返回 null。</summary>
    private Uri? ValidateApiUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ApiStatusIsError = true;
            ApiConnectionStatus = "地址格式不正确，请输入以 http:// 或 https:// 开头的完整地址。";
            return null;
        }

        return uri;
    }

    /// <summary>探测一个地址是否为可用的网易云 API 服务（只判断连通性，不切换、不持久化）。</summary>
    public async Task<bool> TestApiBaseUrlAsync()
    {
        var url = ApiBaseUrl.Trim();
        var uri = ValidateApiUrl(url);
        if (uri is null)
        {
            return false;
        }

        ApiBusy = true;

        try
        {
            var ok = await NeteaseApiClient.ProbeServerAsync(uri, _loginSession.Cookie).ConfigureAwait(false);
            await BackToUiAsync();

            ApiStatusIsError = !ok;
            ApiConnectionStatus = ok
                ? "连接成功：该地址是可用的网易云 API 服务。"
                : "连接失败：该地址无法访问或不是网易云 API 服务。";
            return ok;
        }
        catch (Exception)
        {
            await BackToUiAsync();
            ApiStatusIsError = true;
            ApiConnectionStatus = "连接失败：无法访问该地址。";
            return false;
        }
        finally
        {
            ApiBusy = false;
        }
    }

    /// <summary>
    /// 保存 API 地址：把输入框里的合法地址应用到当前客户端并持久化到 session.json。
    /// 不重新探测——用户先用「测试连接」确认地址可用，保存只负责应用与持久化。
    /// 方法体全量 try/catch：任何异常都转成页面提示，绝不外抛。
    /// （不要把本方法改成非 async 的裸 Task 方法——调用方是 async void 事件处理器，
    /// 同步方法在返回前抛出的异常不会被 Task 捕获，会直接崩溃应用。）
    /// </summary>
    public async Task SaveApiBaseUrlAsync()
    {
        try
        {
            var url = ApiBaseUrl?.Trim() ?? string.Empty;
            var uri = ValidateApiUrl(url);
            if (uri is null)
            {
                return;
            }

            ApplyApiBaseUrl(uri, url);
            _loginSession.SaveApiBaseUrl(url);
            ApiStatusIsError = false;
            ApiConnectionStatus = "已保存：后续请求将发往该服务器。";
        }
        catch (Exception ex)
        {
            ApiStatusIsError = true;
            ApiConnectionStatus = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>判断地址是否为本地 API（localhost / 127.0.0.1 / ::1）。公网地址返回 false。</summary>
    public static bool IsLocalHostUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host is "localhost" or "127.0.0.1" or "::1";
    }

    /// <summary>
    /// 应用本地 API 自动拉起的地址（可能因 3000 被占而落在其他端口）。
    /// 与手动「保存」不同：不持久化到会话（用户切换远程地址后不应被覆盖）。
    /// </summary>
    public void ApplyLocalApiBaseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return;
        }

        ApplyApiBaseUrl(uri, url);
    }

    /// <summary>
    /// 应用新地址：整体替换当前 API 客户端（旧 HttpClient 不能再改 BaseAddress）。
    /// 新客户端复用已保存的登录 cookie，保证切换后登录态保留。
    /// </summary>
    private void ApplyApiBaseUrl(Uri uri, string url)
    {
        // 释放旧客户端（若有进行中的请求，其最终会失败并被友好提示）。
        try
        {
            _httpClient.Dispose();
        }
        catch (Exception)
        {
            // 释放失败不影响切换。
        }

        _httpClient = CreateHttpClient(url);
        _apiClient = new NeteaseApiClient(_httpClient)
        {
            LoginCookie = _loginSession.Cookie,
        };
        OnPropertyChanged(nameof(ApiCurrentServerText));
        StatusText = $"已切换到 API 服务器：{url}";
    }

    /// <summary>
    /// 启动时后台验证当前 API 地址是否可连：不可连时在状态栏提示，
    /// 不阻塞界面加载（首页/搜索等失败也会各自友好提示）。
    /// </summary>
    public async Task VerifyApiConnectionAsync()
    {
        try
        {
            var ok = await _apiClient.IsServerReachableAsync().ConfigureAwait(false);
            await BackToUiAsync();
            if (!ok)
            {
                StatusText = "当前 API 服务不可用，请到「设置」检查 API 地址。";
            }
        }
        catch (Exception)
        {
            await BackToUiAsync();
            StatusText = "无法连接 API 服务，请到「设置」检查 API 地址。";
        }
    }

    // ---- 音频输出：设备选择与独占/共享模式 ----

    /// <summary>当前可用的输出设备列表（系统默认 + 所有活跃渲染端点），供设置页下拉。</summary>
    public ObservableCollection<OutputDevice> OutputDevices { get; } = new();

    private OutputMode _outputMode = OutputMode.Exclusive;

    /// <summary>当前输出模式（独占/共享）。切换后立即生效：正在播放的歌用新模式重新加载。</summary>
    public OutputMode SelectedOutputMode
    {
        get => _outputMode;
        set
        {
            if (SetProperty(ref _outputMode, value))
            {
                _loginSession.SaveOutputMode(value.ToString());
                _playbackService.ShareModeEnabled = value == OutputMode.Shared;
                OnPropertyChanged(nameof(OutputModeText));
                _ = ReloadForOutputChangedAsync();
            }
        }
    }

    private OutputDevice? _selectedOutputDevice;

    /// <summary>当前选择的输出设备（系统默认 = 跟随系统）。切换后立即生效并持久化。</summary>
    public OutputDevice? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (value == null || Equals(_selectedOutputDevice, value))
            {
                return;
            }

            _selectedOutputDevice = value;
            OnPropertyChanged(nameof(SelectedOutputDevice));
            _playbackService.OutputDeviceId = value.Id;
            _loginSession.SaveOutputDevice(value.Id);
            OnPropertyChanged(nameof(OutputDeviceText));
            _ = ReloadForOutputChangedAsync();
        }
    }

    /// <summary>侧栏「输出模式」指示文案：独占/共享。</summary>
    public string OutputModeText => SelectedOutputMode == OutputMode.Shared ? "共享" : "WASAPI Exclusive";

    /// <summary>侧栏「输出设备」指示文案：跟随系统 / 用户选中的设备名。</summary>
    public string OutputDeviceText =>
        SelectedOutputDevice is { IsSystemDefault: false } device
            ? device.DisplayName
            : "默认输出设备";

    /// <summary>
    /// 切换输出设备/模式后：正在播放时用新的输出重新加载当前歌曲，让切换立即生效；
    /// 未播放时只更新状态提示。
    /// </summary>
    private async Task ReloadForOutputChangedAsync()
    {
        if (!IsPlaying && !IsPaused)
        {
            StatusText = $"输出设置已更新：{OutputModeText} · {OutputDeviceText}";
            return;
        }

        var song = SelectedSong ?? Songs.FirstOrDefault();
        if (song == null)
        {
            return;
        }

        StatusText = $"输出已切换为 {OutputModeText} · {OutputDeviceText}，正在重新加载当前歌曲...";
        await PlaySongAsync(song);
    }

    /// <summary>
    /// 枚举当前系统所有活跃的输出设备，并同步下拉列表与当前选择项。
    /// 保留用户之前选择的设备；已拔出的设备从列表移除（选择回退为系统默认）。
    /// 同步执行（本地枚举，不做异步），不进 UI 线程也可安全调用。
    /// </summary>
    public void RefreshOutputDevices()
    {
        var devices = new List<OutputDevice> { OutputDevice.SystemDefault };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                devices.Add(new OutputDevice(device.ID, device.FriendlyName));
            }
        }
        catch (Exception)
        {
            // 枚举失败时仅保留「系统默认」，播放仍走系统默认设备，不影响使用。
        }

        OutputDevices.Clear();
        foreach (var device in devices)
        {
            OutputDevices.Add(device);
        }

        // 恢复上次选择的设备：仍存在则选中它，否则回退系统默认。
        var savedId = _loginSession.OutputDeviceId;
        _selectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Id == savedId) ?? OutputDevice.SystemDefault;
        _playbackService.OutputDeviceId = _selectedOutputDevice.Id;
        OnPropertyChanged(nameof(SelectedOutputDevice));
        OnPropertyChanged(nameof(OutputDeviceText));
    }

    public MainViewModel()
    {
        _loginSession = new LoginSession();
        _uiSyncContext = SynchronizationContext.Current;

        // 从磁盘恢复上次的登录会话、API 地址、音质偏好和播放模式。
        _loginSession.Load();

        // 按保存的 API 地址建 HttpClient；设置页切换地址时复用同一实例（改 BaseAddress）。
        _httpClient = CreateHttpClient(_loginSession.ApiBaseUrl);
        _apiClient = new NeteaseApiClient(_httpClient);
        _playbackService = new AudioPlaybackService();

        _playbackService.IsPlayingChanged += OnIsPlayingChanged;
        _playbackService.IsPausedChanged += OnIsPausedChanged;
        _playbackService.StatusMessageChanged += OnStatusMessageChanged;
        _playbackService.ErrorOccurred += OnPlaybackError;
        _playbackService.PlaybackFinished += OnPlaybackFinished;
        _playbackService.PositionChanged += OnPositionChanged;

        // 后续请求自动携带该 cookie。
        _apiClient.LoginCookie = _loginSession.Cookie;
        _preferredQuality = QualityOptions.FirstOrDefault(o => o.Key == _loginSession.PreferredQuality)
            ?? _preferredQuality;
        _playbackMode = Enum.TryParse<PlaybackMode>(_loginSession.PlaybackMode, out var savedMode)
            ? savedMode
            : PlaybackMode.Sequential;
        _currentSearchTypeOption = SearchTypeOptions.First(o => o.Type == _currentSearchType);

        // 设置页文本框初始值 = 会话里保存的地址。
        _apiBaseUrl = _loginSession.ApiBaseUrl;

        // 恢复「自动启动本地 API」开关状态（设置页切换时持久化）。
        _autoStartLocalApi = _loginSession.AutoStartLocalApi;

        // 恢复音频输出偏好：模式（独占/共享）+ 输出设备（默认系统默认）。
        // 设备枚举发生在 UI 线程启动时（MainWindow_Loaded），此处先应用共享模式与默认设备，
        // 枚举完成后 RefreshOutputDevices 会把已保存的设备 id 选回来。
        _outputMode = Enum.TryParse<OutputMode>(_loginSession.OutputMode, out var savedOutputMode)
            ? savedOutputMode
            : OutputMode.Exclusive;
        _playbackService.ShareModeEnabled = _outputMode == OutputMode.Shared;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 当前歌曲列表页的上下文：决定右键菜单是否显示「删除」。
    /// 仅「我喜欢的音乐」与「我创建的歌单」支持从歌单删除，其余（搜索/专辑/歌手/日推/收藏的歌单）不显示。
    /// </summary>
    private enum ListContext { Other, Liked, CreatedPlaylist }

    private ListContext _currentListContext = ListContext.Other;

    public ObservableCollection<Song> Songs { get; } = new();

    public ObservableCollection<Playlist> Playlists { get; } = new();

    /// <summary>我创建的歌单。</summary>
    public ObservableCollection<Playlist> CreatedPlaylists { get; } = new();

    /// <summary>我收藏的歌单。</summary>
    public ObservableCollection<Playlist> CollectedPlaylists { get; } = new();

    /// <summary>
    /// 底部收藏弹窗的歌单候选：我创建的歌单（specialType==0，不含「我喜欢的音乐」）。
    /// 点击收藏按钮时懒加载；未登录或加载失败时为空。
    /// </summary>
    public ObservableCollection<Playlist> CreatedPlaylistsForCollect { get; } = new();

    /// <summary>
    /// 收藏窗是否已完成一次加载（登录检查 + 歌单拉取都走过）。
    /// 为 true 时 UI 才显示「没有可收藏的歌单」空提示，避免窗口刚打开时闪烁。
    /// </summary>
    public bool CollectPlaylistsLoaded
    {
        get => _collectPlaylistsLoaded;
        private set
        {
            if (SetProperty(ref _collectPlaylistsLoaded, value))
            {
                OnPropertyChanged(nameof(CollectNoPlaylistsText));
                OnPropertyChanged(nameof(ShowCollectEmptyText));
            }
        }
    }

    private bool _collectPlaylistsLoaded;

    /// <summary>收藏窗里的空状态提示：未登录 / 没有创建的歌单 / 加载失败。</summary>
    public string CollectNoPlaylistsText
    {
        get
        {
            if (!_loginSession.HasSession)
            {
                return "请先登录，才能收藏歌曲到歌单。";
            }

            if (CreatedPlaylistsForCollect.Count == 0)
            {
                return "你还没有创建的歌单，先创建歌单再收藏歌曲。";
            }

            return string.Empty;
        }
    }

    /// <summary>收藏窗是否有候选歌单（有则显示列表，无则显示 CollectNoPlaylistsText）。</summary>
    public bool HasCollectPlaylists => CreatedPlaylistsForCollect.Count > 0;

    /// <summary>
    /// 收藏窗是否显示空状态提示：仅当完成过一次加载且没有候选歌单时。
    /// 避免窗口刚打开（尚未加载完）时误显「没有可收藏的歌单」。
    /// </summary>
    public bool ShowCollectEmptyText => CollectPlaylistsLoaded && !HasCollectPlaylists;

    /// <summary>首页推荐歌单（/personalized）。</summary>
    public ObservableCollection<Playlist> RecommendedPlaylists { get; } = new();

    /// <summary>
    /// 首页第一栏快捷卡片（我喜欢的音乐 / 今日推荐 / 私人雷达 / 日系 / 欧美）。
    /// UI 用 ItemsControl + UniformGrid 等宽排列，任何窗口宽度都不换行。
    /// </summary>
    public ObservableCollection<HomeQuickCard> HomeQuickCards { get; } = [];

    /// <summary>专辑搜索结果。</summary>
    public ObservableCollection<Album> Albums { get; } = new();

    /// <summary>歌手搜索结果。</summary>
    public ObservableCollection<Artist> Artists { get; } = new();

    /// <summary>
    /// 歌曲列表的显示源：默认就是 Songs 的可过滤视图。
    /// 在「喜欢的音乐」里输入搜索词时，过滤这个视图（只搜当前列表，不搜全站）。
    /// </summary>
    public ICollectionView DisplaySongs
    {
        get
        {
            if (_songsView == null)
            {
                _songsView = CollectionViewSource.GetDefaultView(Songs);
            }

            return _songsView;
        }
    }

    /// <summary>当前歌曲列表内的过滤词（对搜索/喜欢/歌单/专辑/日推列表统一生效）。</summary>
    public string ListFilterText
    {
        get => _likedSearchText;
        set
        {
            if (SetProperty(ref _likedSearchText, value))
            {
                ApplyListFilter();
            }
        }
    }

    public Song? SelectedSong
    {
        get => _selectedSong;
        set
        {
            if (SetProperty(ref _selectedSong, value))
            {
                OnPropertyChanged(nameof(CanPlay));
            }
        }
    }

    /// <summary>
    /// 当前列表是否允许右键删除歌曲：仅「我喜欢的音乐」和「我创建的歌单」。
    /// 右键菜单据此显示/隐藏「删除」项。
    /// </summary>
    public bool CanDeleteFromCurrentList => _currentListContext != ListContext.Other;

    /// <summary>记录当前歌曲列表的来源上下文（喜欢的音乐/创建的歌单/其他）。</summary>
    private void SetListContext(ListContext context)
    {
        _currentListContext = context;
        OnPropertyChanged(nameof(CanDeleteFromCurrentList));
    }

    /// <summary>统一搜索关键词（搜索页专用）。</summary>
    public string SearchKeyword
    {
        get => _searchKeyword;
        set => SetProperty(ref _searchKeyword, value);
    }

    /// <summary>首页搜索框关键词（与搜索页隔离，不残留）。</summary>
    public string HomeSearchText
    {
        get => _homeSearchText;
        set => SetProperty(ref _homeSearchText, value);
    }

    /// <summary>当前搜索类型（歌曲/歌单/专辑/歌手）。</summary>
    public SearchType CurrentSearchType => _currentSearchType;

    /// <summary>UI 类型选择器绑定：用户点击类型时触发重新搜索。</summary>
    public SearchTypeOption? CurrentSearchTypeOption
    {
        get => _currentSearchTypeOption;
        set
        {
            if (value != null && value.Type != _currentSearchType)
            {
                SwitchSearchType(value.Type, reSearch: true);
            }
        }
    }

    public string CurrentSongText
    {
        get => _currentSongText;
        set => SetProperty(ref _currentSongText, value);
    }

    /// <summary>当前播放歌曲的封面（底部栏缩略图用）。</summary>
    public string CurrentSongCoverUrl
    {
        get => _currentSongCoverUrl;
        private set => SetProperty(ref _currentSongCoverUrl, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>
    /// 当前播放歌曲是否在「我喜欢的音乐」歌单里。决定底部栏红心图标：
    /// 已喜欢 → 红心收藏后的图标；未喜欢 → 红心前的图标。
    /// 仅在需要登录且已加载喜欢列表时才有意义（未登录/未加载保持默认不红）。
    /// </summary>
    public bool CurrentSongLiked
    {
        get => _currentSongLiked;
        private set
        {
            if (SetProperty(ref _currentSongLiked, value))
            {
                OnPropertyChanged(nameof(LikedToolTip));
            }
        }
    }

    /// <summary>红心按钮 ToolTip：当前歌曲喜欢状态提示。</summary>
    public string LikedToolTip => _currentSongLiked ? "取消喜欢" : "喜欢";

    /// <summary>收藏弹窗里显示的歌曲名（右键所选优先，其次当前播放）。</summary>
    public string CollectPopupSongText => _pendingCollectSongId > 0
        ? $"将《{_pendingCollectSongText}》收藏到："
        : _currentSongId > 0
            ? $"将《{CurrentSongText}》收藏到："
            : "当前没有正在播放的歌曲";

    private bool _currentSongLiked;

    private string _trackListTitle = string.Empty;

    /// <summary>歌曲列表页标题（歌单名/专辑名/歌手名/今日推荐/雷达名）。</summary>
    public string TrackListTitle
    {
        get => _trackListTitle;
        set => SetProperty(ref _trackListTitle, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanPlay));
            }
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseButtonText));
                OnPropertyChanged(nameof(PlayPauseIcon));
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(PlayPauseButtonText));
                OnPropertyChanged(nameof(PlayPauseIcon));
            }
        }
    }

    public bool CanPlay => !IsBusy && Songs.Count > 0;

    public string PlayPauseButtonText => IsPaused ? "继续" : IsPlaying ? "暂停" : "播放";

    /// <summary>播放/暂停按钮图标（Segoe MDL2）：暂停中=播放(E768)、播放中=暂停(E769)。</summary>
    public string PlayPauseIcon => IsPaused ? "" : IsPlaying ? "" : "";

    /// <summary>播放进度比例，0.0～1.0，用于 Slider 绑定。</summary>
    public double PositionRatio
    {
        get => _positionRatio;
        set
        {
            if (SetProperty(ref _positionRatio, value))
            {
                OnPropertyChanged(nameof(PositionText));
            }
        }
    }

    public string PositionText
    {
        get => _positionText;
        private set => SetProperty(ref _positionText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    /// <summary>音量，0.0～1.0，用于音量 Slider 绑定。</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, value))
            {
                _playbackService.Volume = (float)value;
            }
        }
    }

    /// <summary>
    /// 播放定时器回写 PositionRatio 时置为 true。Slider 的 ValueChanged 据此区分
    /// 「定时器被动跟随」与「用户点击/拖拽主动修改」，避免回写触发多余的 Seek。
    /// </summary>
    public bool IsPositionUpdateFromPlayback
    {
        get => _isPositionUpdateFromPlayback;
        private set => SetProperty(ref _isPositionUpdateFromPlayback, value);
    }

    /// <summary>登录相关 UI 状态（二维码、昵称、登录态）。</summary>
    public LoginViewModel LoginState => _loginState ??= new LoginViewModel();

    /// <summary>统一搜索入口：按当前搜索类型分发到对应的 core 方法。</summary>
    public async Task SearchAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_searchKeyword))
        {
            StatusText = "请输入搜索关键词。";
            return;
        }

        var myRequestId = ++_searchRequestId;

        switch (_currentSearchType)
        {
            case SearchType.Song:
                await SearchSongsCoreAsync(myRequestId);
                break;
            case SearchType.Playlist:
                await SearchPlaylistsCoreAsync(myRequestId);
                break;
            case SearchType.Album:
                await SearchAlbumsCoreAsync(myRequestId);
                break;
            case SearchType.Artist:
                await SearchArtistsCoreAsync(myRequestId);
                break;
        }
    }

    /// <summary>当前搜索请求仍是最新的（期间没有切换类型发起新搜索）。</summary>
    private bool IsCurrentSearchRequest(int myRequestId) => myRequestId == _searchRequestId;

    private async Task SearchSongsCoreAsync(int myRequestId)
    {
        SetBusy(true);

        try
        {
            // 搜索开始前清空当前列表（搜索列表不缓存），避免加载期间残留上一页（如「我喜欢的音乐」）。
            SetListContext(ListContext.Other);
            Songs.Clear();

            var songs = await FetchUiAsync(_apiClient.SearchSongsAsync(_searchKeyword));
            if (!IsCurrentSearchRequest(myRequestId))
            {
                return;
            }

            var detailedSongs = await FetchUiAsync(_apiClient.GetSongsDetailAsync(songs.Select(song => song.Id)));
            if (!IsCurrentSearchRequest(myRequestId))
            {
                return;
            }

            var coverBySongId = detailedSongs.ToDictionary(
                song => song.Id,
                song => song.CoverUrl);

            foreach (var song in songs)
            {
                if (coverBySongId.TryGetValue(song.Id, out var coverUrl))
                {
                    if (song.Album != null)
                    {
                        song.Album.CoverUrl = coverUrl;
                    }

                    if (song.SearchAlbum != null)
                    {
                        song.SearchAlbum.CoverUrl = coverUrl;
                    }
                }

                Songs.Add(song);
            }

            SelectedSong = Songs.FirstOrDefault();
            _currentIndex = -1;
            StatusText = $"搜索到 {songs.Count} 首歌曲，双击歌曲或点击播放开始。";
        }
        catch (Exception ex)
        {
            if (IsCurrentSearchRequest(myRequestId))
            {
                StatusText = GetFriendlyMessage(ex);
            }
        }
        finally
        {
            if (IsCurrentSearchRequest(myRequestId))
            {
                SetBusy(false);
            }
        }
    }

    private async Task SearchPlaylistsCoreAsync(int myRequestId)
    {
        SetBusy(true);

        try
        {
            var playlists = await FetchUiAsync(_apiClient.SearchPlaylistsAsync(_searchKeyword, 50));
            if (!IsCurrentSearchRequest(myRequestId))
            {
                return;
            }

            Playlists.Clear();
            foreach (var playlist in playlists)
            {
                Playlists.Add(playlist);
            }

            StatusText = $"搜索到 {playlists.Count} 个歌单，双击歌单加载并播放。";
        }
        catch (Exception ex)
        {
            if (IsCurrentSearchRequest(myRequestId))
            {
                StatusText = GetFriendlyMessage(ex);
            }
        }
        finally
        {
            if (IsCurrentSearchRequest(myRequestId))
            {
                SetBusy(false);
            }
        }
    }

    private async Task SearchAlbumsCoreAsync(int myRequestId)
    {
        SetBusy(true);

        try
        {
            var albums = await FetchUiAsync(_apiClient.SearchAlbumsAsync(_searchKeyword, 50));
            if (!IsCurrentSearchRequest(myRequestId))
            {
                return;
            }

            Albums.Clear();
            foreach (var album in albums)
            {
                Albums.Add(album);
            }

            StatusText = $"搜索到 {albums.Count} 张专辑，双击专辑加载歌曲。";
        }
        catch (Exception ex)
        {
            if (IsCurrentSearchRequest(myRequestId))
            {
                StatusText = GetFriendlyMessage(ex);
            }
        }
        finally
        {
            if (IsCurrentSearchRequest(myRequestId))
            {
                SetBusy(false);
            }
        }
    }

    private async Task SearchArtistsCoreAsync(int myRequestId)
    {
        SetBusy(true);

        try
        {
            var artists = await FetchUiAsync(_apiClient.SearchArtistsAsync(_searchKeyword, 50));
            if (!IsCurrentSearchRequest(myRequestId))
            {
                return;
            }

            Artists.Clear();
            foreach (var artist in artists)
            {
                Artists.Add(artist);
            }

            StatusText = $"搜索到 {artists.Count} 位歌手，双击歌手加载热门歌曲。";
        }
        catch (Exception ex)
        {
            if (IsCurrentSearchRequest(myRequestId))
            {
                StatusText = GetFriendlyMessage(ex);
            }
        }
        finally
        {
            if (IsCurrentSearchRequest(myRequestId))
            {
                SetBusy(false);
            }
        }
    }

    /// <summary>
    /// 加载当前用户的喜欢音乐列表（需登录）。
    /// 策略：先显示本地缓存（秒开），后台拉最新列表（顺序与 App 一致）替换。
    /// </summary>
    /// <summary>防重入：喜欢的音乐加载中（独立于全局 IsBusy，避免播放/搜索时点导航被静默吞掉）。</summary>
    private bool _loadingLikedSongs;

    public async Task LoadLikedSongsAsync()
    {
        if (_loadingLikedSongs)
        {
            return;
        }

        _loadingLikedSongs = true;

        try
        {
            if (!_loginSession.HasSession)
            {
                StatusText = "请先登录，才能查看喜欢的音乐。";
                return;
            }

            // 旧版本登录的会话可能没有保存 userId，这里用 /login/status 自动补齐。
            if (_loginSession.UserId <= 0)
            {
                var restored = await RestoreUserIdAsync();
                if (!restored)
                {
                    StatusText = "登录状态失效，请重新登录后再查看喜欢的音乐。";
                    return;
                }
            }

            // 先显示缓存（如果有），保证打开秒开。
            _likedCache.Load();
            if (_likedCache.HasCache)
            {
                ShowLikedSongs(_likedCache.Songs!);
                StatusText = "正在刷新喜欢的音乐...";
            }
            else
            {
                StatusText = "正在加载喜欢的音乐...";
            }

            SetBusy(true);

            try
            {
                // 拉最新列表（顺序与 App 一致），后台增量对齐：已显示的行不动（封面不重载），
                // 新喜欢的插入、取消喜欢的移除，避免整清重建的闪烁。
                var songs = await FetchUiAsync(_apiClient.GetLikedSongsAsync(_loginSession.UserId));
                SetListContext(ListContext.Liked);
                MergeSongList(Songs, songs);
                SelectedSong = Songs.FirstOrDefault();
                _currentIndex = -1;
                ResetSearchKeyword();

                // 同步喜欢歌曲 id 集合（底部红心图标状态源）。
                _likedSongIds.Clear();
                foreach (var song in songs)
                {
                    _likedSongIds.Add(song.Id);
                }

                if (_currentSongId > 0)
                {
                    CurrentSongLiked = _likedSongIds.Contains(_currentSongId);
                }

                // 回填首页「我喜欢的音乐」卡片封面。
                var likedCard = HomeQuickCards.FirstOrDefault(c => c.Kind == HomeQuickCardKind.Liked);
                if (likedCard is not null)
                {
                    likedCard.CoverUrl = songs.FirstOrDefault()?.CoverUrl ?? string.Empty;
                }

                _likedCache.Save(songs);
                StatusText = $"已加载 {songs.Count} 首喜欢的音乐，双击歌曲开始播放。";
            }
            catch (Exception ex)
            {
                // 刷新失败时保留已显示的缓存，只提示错误。
                if (!_likedCache.HasCache)
                {
                    StatusText = GetFriendlyMessage(ex);
                }
                else
                {
                    StatusText = $"刷新失败，显示的是上次缓存的 {_likedCache.Songs!.Count} 首。{GetFriendlyMessage(ex)}";
                }
            }
            finally
            {
                SetBusy(false);
            }
        }
        finally
        {
            _loadingLikedSongs = false;
        }
    }

    /// <summary>把歌曲列表填充到显示集合，并保持 App 顺序。</summary>
    private void ShowLikedSongs(IReadOnlyList<Song> songs)
    {
        SetListContext(ListContext.Liked);
        Songs.Clear();
        foreach (var song in songs)
        {
            Songs.Add(song);
        }

        SelectedSong = Songs.FirstOrDefault();
        _currentIndex = -1;

        // 同步喜欢歌曲 id 集合（底部红心图标状态源）：
        // 当前播放的歌如果出现在喜欢列表里，红心图标立即变红。
        _likedSongIds.Clear();
        foreach (var song in songs)
        {
            _likedSongIds.Add(song.Id);
        }

        if (_currentSongId > 0)
        {
            CurrentSongLiked = _likedSongIds.Contains(_currentSongId);
        }

        // 回填首页「我喜欢的音乐」卡片封面。
        var likedCard = HomeQuickCards.FirstOrDefault(c => c.Kind == HomeQuickCardKind.Liked);
        if (likedCard is not null)
        {
            likedCard.CoverUrl = songs.FirstOrDefault()?.CoverUrl ?? string.Empty;
        }

        // 喜欢列表不是全站搜索结果，清空搜索词并复位类型。
        ResetSearchKeyword();
    }

    /// <summary>按过滤词过滤当前歌曲列表（按歌名/歌手，忽略大小写）。</summary>
    private void ApplyListFilter()
    {
        var keyword = _likedSearchText.Trim();
        DisplaySongs.Filter = string.IsNullOrEmpty(keyword)
            ? null
            : new Predicate<object>(item =>
            {
                if (item is not Song song)
                {
                    return false;
                }

                return song.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || song.ArtistNames.Contains(keyword, StringComparison.OrdinalIgnoreCase);
            });

        DisplaySongs.Refresh();
    }

    /// <summary>清空列表内过滤词。</summary>
    public void ClearListFilter()
    {
        if (string.IsNullOrEmpty(_likedSearchText))
        {
            return;
        }

        _likedSearchText = string.Empty;
        OnPropertyChanged(nameof(ListFilterText));
        ApplyListFilter();
    }

    /// <summary>防重入：我的歌单加载中（独立于全局 IsBusy，避免播放/搜索时点导航被静默吞掉）。</summary>
    private bool _loadingMyPlaylists;

    /// <summary>加载我的歌单（创建 + 收藏，分组展示）。需登录。</summary>
    public async Task LoadMyPlaylistsAsync()
    {
        if (_loadingMyPlaylists)
        {
            return;
        }

        _loadingMyPlaylists = true;

        try
        {
            if (!_loginSession.HasSession)
            {
                StatusText = "请先登录，才能查看我的歌单。";
                return;
            }

            // 旧会话可能缺 userId，自动补齐。
            if (_loginSession.UserId <= 0)
            {
                var restored = await RestoreUserIdAsync();
                if (!restored)
                {
                    StatusText = "登录状态失效，请重新登录后再查看我的歌单。";
                    return;
                }
            }

            // 先显示本地缓存（如果有），保证打开秒开，图片不重新加载。
            _myPlaylistCache.Load();
            if (_myPlaylistCache.HasCache)
            {
                ShowMyPlaylists(_myPlaylistCache.Created, _myPlaylistCache.Collected);
                StatusText = "正在刷新我的歌单...";
            }
            else
            {
                StatusText = "正在加载我的歌单...";
            }

            SetBusy(true);

            try
            {
                var playlists = await FetchUiAsync(_apiClient.GetUserPlaylistsAsync(_loginSession.UserId));

                // 增量对齐：已有歌单行不动（缩略图不重载），新增的追加、消失的移除。
                MergeMyPlaylists(playlists);

                _myPlaylistCache.Save(CreatedPlaylists, CollectedPlaylists);
                StatusText = $"我创建了 {CreatedPlaylists.Count} 个歌单，收藏了 {CollectedPlaylists.Count} 个歌单，双击歌单进入歌曲列表。";
            }
            catch (Exception ex)
            {
                StatusText = GetFriendlyMessage(ex);
            }
            finally
            {
                SetBusy(false);
            }
        }
        finally
        {
            _loadingMyPlaylists = false;
        }
    }

    /// <summary>把缓存的我创建/收藏歌单列表填充到两个显示集合（秒开用）。</summary>
    private void ShowMyPlaylists(IReadOnlyList<Playlist>? created, IReadOnlyList<Playlist>? collected)
    {
        CreatedPlaylists.Clear();
        if (created is not null)
        {
            foreach (var playlist in created)
            {
                CreatedPlaylists.Add(playlist);
            }
        }

        CollectedPlaylists.Clear();
        if (collected is not null)
        {
            foreach (var playlist in collected)
            {
                CollectedPlaylists.Add(playlist);
            }
        }
    }

    /// <summary>
    /// 增量对齐「我的歌单」列表：已有歌单行保持不动（缩略图不重载），
    /// 新增的歌单追加到末尾，服务端已消失的歌单移除。避免整清重建导致的闪烁。
    /// </summary>
    private void MergeMyPlaylists(IReadOnlyList<Playlist> fresh)
    {
        // 先按创建/收藏分组，并保持服务端返回顺序。
        var freshCreated = new List<Playlist>();
        var freshCollected = new List<Playlist>();
        foreach (var playlist in fresh)
        {
            if (playlist.Creator?.UserId == _loginSession.UserId)
            {
                freshCreated.Add(playlist);
            }
            else
            {
                freshCollected.Add(playlist);
            }
        }

        MergePlaylistCollection(CreatedPlaylists, freshCreated);
        MergePlaylistCollection(CollectedPlaylists, freshCollected);
    }

    /// <summary>
    /// 按歌单 id 对齐一个显示集合到目标列表（增量）：
    /// 目标里已有的行 → 若元信息（名称/封面/歌曲数）完全一致则保持原对象不动（缩略图不重载），
    /// 不一致才替换为新对象（如歌曲数变化）；新增的歌单按目标顺序追加，消失的移除。
    /// </summary>
    private static void MergePlaylistCollection(ObservableCollection<Playlist> display, IReadOnlyList<Playlist> target)
    {
        var targetById = target.ToDictionary(p => p.Id, p => p);

        // 1) 移除已不在目标中的行（从后往前避免索引错位）。
        for (var i = display.Count - 1; i >= 0; i--)
        {
            if (!targetById.ContainsKey(display[i].Id))
            {
                display.RemoveAt(i);
            }
        }

        // 2) 逐项对齐目标顺序：已有且元信息未变的行跳过（封面不重载）；
        //    已有但元信息变化的行替换为新对象；目标里有、显示里没有的新歌单插入。
        var dispPos = 0;
        for (var ti = 0; ti < target.Count; ti++)
        {
            var targetItem = target[ti];
            if (dispPos < display.Count && display[dispPos].Id == targetItem.Id)
            {
                // 元信息不一致才替换，否则保持原对象（缩略图不重载）。
                if (!PlaylistMetaEquals(display[dispPos], targetItem))
                {
                    display[dispPos] = targetItem;
                }

                dispPos++;
                continue;
            }

            var existingPos = -1;
            for (var j = dispPos; j < display.Count; j++)
            {
                if (display[j].Id == targetItem.Id)
                {
                    existingPos = j;
                    break;
                }
            }

            if (existingPos >= 0)
            {
                display.RemoveAt(existingPos);
                display.Insert(dispPos, targetItem);
            }
            else
            {
                display.Insert(dispPos, targetItem);
            }

            dispPos++;
        }

        // 3) 目标顺序里没有的行（上面移除后不应有，保险清理）。
        if (display.Count > target.Count)
        {
            var targetIds = new HashSet<long>(target.Select(p => p.Id));
            for (var i = display.Count - 1; i >= 0; i--)
            {
                if (!targetIds.Contains(display[i].Id))
                {
                    display.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>两个歌单的显示用元信息（名称/封面/歌曲数）是否一致。缩略图 ImageSource 走同一个 URL 时会命中缓存，无需特殊比较。</summary>
    private static bool PlaylistMetaEquals(Playlist a, Playlist b)
        => a.Name == b.Name
           && a.CoverUrl == b.CoverUrl
           && a.TrackCount == b.TrackCount
           && a.PlayCount == b.PlayCount;

    /// <summary>首页进入时加载：快捷卡片 + 推荐歌单 + 喜欢封面 + 日推封面。不设 IsBusy（不动 Songs）。</summary>
    public async Task LoadHomeAsync()
    {
        // 先同步建卡（保证喜欢/日推/雷达卡片始终就位），再填充各卡片内容。
        EnsureHomeQuickCards();
        await LoadRecommendedPlaylistsAsync();
        RefreshLikedCoverFromCache();
        await LoadDailyPreviewAsync();
        await RefreshDailyPlaylistCardsAsync();
    }

    /// <summary>
    /// 同步确保第一栏快捷卡片已创建（喜欢/日推/三个雷达）。
    /// 必须在 LoadDailyPreviewAsync 之前调用，否则日推封面写不进卡片。
    /// </summary>
    public void EnsureHomeQuickCards()
    {
        if (HomeQuickCards.Count > 0)
        {
            return;
        }

        HomeQuickCards.Add(new HomeQuickCard
        {
            Kind = HomeQuickCardKind.Liked,
            Title = "我喜欢的音乐",
            Subtitle = "所有收藏歌曲",
        });
        HomeQuickCards.Add(new HomeQuickCard
        {
            Kind = HomeQuickCardKind.Daily,
            Title = "今日推荐",
            Subtitle = "登录后查看",
        });

        foreach (var (id, title) in DailyPlaylists)
        {
            HomeQuickCards.Add(new HomeQuickCard
            {
                Kind = HomeQuickCardKind.DailyPlaylist,
                PlaylistId = id,
                Title = title,
                Subtitle = "每日更新",
            });
        }
    }

    /// <summary>
    /// 后台刷新三个雷达歌单的封面与副标题（每日更新的歌单 id 稳定，封面会变）。
    /// 静默失败：拿不到雷达封面时保留原封面，不影响首页其他部分。
    /// </summary>
    public async Task RefreshDailyPlaylistCardsAsync()
    {
        foreach (var card in HomeQuickCards.Where(c => c.Kind == HomeQuickCardKind.DailyPlaylist))
        {
            try
            {
                var playlist = await FetchUiAsync(_apiClient.GetPlaylistAsync(card.PlaylistId));
                if (!string.IsNullOrWhiteSpace(playlist.CoverUrl))
                {
                    card.CoverUrl = playlist.CoverUrl;
                }

                card.Subtitle = $"每日更新 · {playlist.TrackCount} 首";
            }
            catch (Exception)
            {
                // 雷达歌单拿不到封面时静默保留占位/旧封面，不影响首页。
            }
        }
    }

    /// <summary>
    /// 加载首页推荐歌单（只加载一次）。用首页发现页个性化推荐（/homepage/block/page
    /// 的 PLAYLIST_RCMD 块）：已登录时贴合用户口味，未登录返回通用推荐。
    /// </summary>
    public async Task LoadRecommendedPlaylistsAsync()
    {
        if (_recommendedLoaded)
        {
            return;
        }

        try
        {
            await ReloadRecommendedPlaylistsCoreAsync(refresh: false);
            _recommendedLoaded = true;
        }
        catch (Exception ex)
        {
            StatusText = GetFriendlyMessage(ex);
        }
    }

    /// <summary>
    /// 刷新推荐歌单：/personalized 源接口内容固定，无法换一批。
    /// 改用 /homepage/block/page?refresh=true 重新拉取个性化推荐，
    /// 换一批仍然贴合用户口味的歌单。
    /// </summary>
    public async Task RefreshRecommendedPlaylistsAsync()
    {
        try
        {
            await ReloadRecommendedPlaylistsCoreAsync(refresh: true);
            StatusText = "推荐歌单已换一批。";
        }
        catch (Exception ex)
        {
            StatusText = GetFriendlyMessage(ex);
        }
    }

    private async Task ReloadRecommendedPlaylistsCoreAsync(bool refresh)
    {
        var playlists = await FetchUiAsync(_apiClient.GetHomepageRecommendedPlaylistsAsync(refresh));
        RecommendedPlaylists.Clear();
        foreach (var playlist in playlists)
        {
            RecommendedPlaylists.Add(playlist);
        }
    }

    /// <summary>加载今日推荐封面（已登录才拉日推）。结果写入第一栏「今日推荐」卡片。</summary>
    public async Task LoadDailyPreviewAsync()
    {
        EnsureHomeQuickCards();
        var dailyCard = HomeQuickCards.FirstOrDefault(c => c.Kind == HomeQuickCardKind.Daily);

        if (!_loginSession.HasSession)
        {
            _dailySongs = null;
            if (dailyCard is not null)
            {
                dailyCard.CoverUrl = string.Empty;
                dailyCard.Subtitle = "登录后查看";
            }

            return;
        }

        try
        {
            _dailySongs = await FetchUiAsync(_apiClient.GetDailySongsAsync());
            if (dailyCard is not null)
            {
                dailyCard.CoverUrl = _dailySongs.FirstOrDefault()?.CoverUrl ?? string.Empty;
                dailyCard.Subtitle = _dailySongs.Count > 0 ? $"为你推荐 {_dailySongs.Count} 首" : "今日暂无推荐";
            }
        }
        catch (Exception ex)
        {
            _dailySongs = null;
            if (dailyCard is not null)
            {
                dailyCard.CoverUrl = string.Empty;
                dailyCard.Subtitle = GetFriendlyMessage(ex);
            }
        }
    }

    /// <summary>从喜欢音乐磁盘缓存读第一首封面（不打 API、不动 Songs），写入第一栏「我喜欢的音乐」卡片。</summary>
    public void RefreshLikedCoverFromCache()
    {
        EnsureHomeQuickCards();
        _likedCache.Load();
        var cover = _likedCache.Songs?.FirstOrDefault()?.CoverUrl ?? string.Empty;
        var card = HomeQuickCards.FirstOrDefault(c => c.Kind == HomeQuickCardKind.Liked);
        if (card is not null)
        {
            card.CoverUrl = cover;
        }

        // 把缓存里的喜欢 id 同步到内存集合：未进「喜欢列表」页时红心图标也有正确初始状态。
        if (_likedSongIds.Count == 0 && _likedCache.HasCache)
        {
            foreach (var song in _likedCache.Songs!)
            {
                _likedSongIds.Add(song.Id);
            }

            if (_currentSongId > 0)
            {
                CurrentSongLiked = _likedSongIds.Contains(_currentSongId);
            }
        }
    }

    /// <summary>点首页「今日推荐」卡片：拉日推填入 Songs（不自动播放）。失败时提示且不清队列。</summary>
    public async Task LoadDailySongsToQueueAsync()
    {
        if (!_loginSession.HasSession)
        {
            StatusText = "请先登录，才能查看今日推荐。";
            return;
        }

        try
        {
            // 今日推荐（主页缓存，仅内存不落盘）在拉取/回填前先清空列表，避免加载期间残留上一页歌曲。
            SetListContext(ListContext.Other);
            Songs.Clear();

            if (_dailySongs is not { Count: > 0 })
            {
                _dailySongs = await FetchUiAsync(_apiClient.GetDailySongsAsync());
                var dailyCard = HomeQuickCards.FirstOrDefault(c => c.Kind == HomeQuickCardKind.Daily);
                if (dailyCard is not null)
                {
                    dailyCard.CoverUrl = _dailySongs.FirstOrDefault()?.CoverUrl ?? string.Empty;
                    dailyCard.Subtitle = $"为你推荐 {_dailySongs.Count} 首";
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = GetFriendlyMessage(ex);
            return;
        }

        if (_dailySongs.Count == 0)
        {
            StatusText = "今日推荐为空。";
            return;
        }

        SetListContext(ListContext.Other);
        foreach (var song in _dailySongs)
        {
            Songs.Add(song);
        }

        SelectedSong = Songs.FirstOrDefault();
        _currentIndex = -1;
        ResetSearchKeyword();
        TrackListTitle = "今日推荐";
        StatusText = $"今日推荐已加载 {Songs.Count} 首，双击歌曲开始播放。";
    }

    /// <summary>登出时清空首页日推数据。</summary>
    public void ClearHomeDailyData()
    {
        _dailySongs = null;
        var dailyCard = HomeQuickCards.FirstOrDefault(c => c.Kind == HomeQuickCardKind.Daily);
        if (dailyCard is not null)
        {
            dailyCard.CoverUrl = string.Empty;
            dailyCard.Subtitle = "登录后查看";
        }
    }
    /// <summary>
    /// 红心按钮：喜欢/取消喜欢当前播放歌曲（写入「我喜欢的音乐」）。
    /// 需登录。成功后更新底部红心图标与喜欢缓存，保持跨页状态一致。
    /// </summary>
    public async Task ToggleLikedAsync()
    {
        if (_currentSongId <= 0)
        {
            StatusText = "当前没有正在播放的歌曲。";
            return;
        }

        if (_isLikeActionInFlight)
        {
            return;
        }

        if (!_loginSession.HasSession)
        {
            StatusText = "请先登录，才能喜欢歌曲。";
            return;
        }

        _isLikeActionInFlight = true;

        try
        {
            var target = !_likedSongIds.Contains(_currentSongId);
            await FetchUiAsync(_apiClient.LikeSongAsync(_currentSongId, target));

            // 更新内存喜欢集合与图标。
            if (target)
            {
                _likedSongIds.Add(_currentSongId);
            }
            else
            {
                _likedSongIds.Remove(_currentSongId);
            }

            CurrentSongLiked = target;

            // 同步本地喜欢缓存：重新拉喜欢列表太重，直接在当前缓存首部插入/删除。
            _likedCache.Load();
            if (_likedCache.HasCache)
            {
                // 只在当前显示列表里能拿到这首歌曲对象时才更新缓存；
                // 列表不含当前歌（导航换页后）时跳过，下次进喜欢列表页全量刷新会修正缓存。
                var current = Songs.FirstOrDefault(s => s.Id == _currentSongId);
                if (current is not null)
                {
                    var cacheSongs = _likedCache.Songs!.ToList();
                    if (target && cacheSongs.All(s => s.Id != current.Id))
                    {
                        cacheSongs.Insert(0, current);
                    }
                    else if (!target)
                    {
                        cacheSongs.RemoveAll(s => s.Id == current.Id);
                    }

                    _likedCache.Save(cacheSongs);
                }
            }

            StatusText = target
                ? $"已喜欢《{CurrentSongText}》"
                : $"已取消喜欢《{CurrentSongText}》";
        }
        catch (Exception ex)
        {
            StatusText = GetFriendlyMessage(ex);
        }
        finally
        {
            _isLikeActionInFlight = false;
        }
    }

    /// <summary>
    /// 从当前列表删除一首歌曲：仅「我喜欢的音乐」（取消喜欢）与「我创建的歌单」支持。
    /// 从 API 删除后同步移除本地列表项与喜欢缓存/集合。
    /// </summary>
    public async Task RemoveCurrentListSongAsync(Song song)
    {
        if (song is null)
        {
            return;
        }

        if (_isLikeActionInFlight)
        {
            return;
        }

        if (!_loginSession.HasSession)
        {
            StatusText = "请先登录，才能删除歌曲。";
            return;
        }

        _isLikeActionInFlight = true;

        try
        {
            switch (_currentListContext)
            {
                case ListContext.Liked:
                    // 我喜欢的音乐 = 取消喜欢（红心熄灭）。
                    await FetchUiAsync(_apiClient.LikeSongAsync(song.Id, false));
                    _likedSongIds.Remove(song.Id);
                    if (_currentSongId == song.Id)
                    {
                        CurrentSongLiked = false;
                    }

                    RemoveSongFromLikedCache(song.Id);
                    Songs.Remove(song);
                    StatusText = $"已从「我喜欢的音乐」删除《{song.DisplayName}》";
                    break;

                case ListContext.CreatedPlaylist:
                    if (_currentPlaylistId <= 0)
                    {
                        StatusText = "当前歌单信息不完整，无法删除。";
                        return;
                    }

                    await FetchUiAsync(_apiClient.RemoveSongFromPlaylistAsync(_currentPlaylistId, song.Id));
                    Songs.Remove(song);
                    // 同步歌单缓存：下次打开该歌单缓存里不再有已删除的歌。
                    _playlistCache.Load();
                    _playlistCache.RemoveSong(_currentPlaylistId, song.Id);
                    StatusText = $"已从当前歌单删除《{song.DisplayName}》";
                    break;

                default:
                    StatusText = "当前列表不支持删除歌曲。";
                    return;
            }
        }
        catch (Exception ex)
        {
            StatusText = GetFriendlyMessage(ex);
        }
        finally
        {
            _isLikeActionInFlight = false;
        }
    }

    /// <summary>把歌曲 id 从本地喜欢缓存中移除（仅当缓存存在时）。</summary>
    private void RemoveSongFromLikedCache(long songId)
    {
        _likedCache.Load();
        if (!_likedCache.HasCache)
        {
            return;
        }

        var cacheSongs = _likedCache.Songs!.ToList();
        if (cacheSongs.RemoveAll(s => s.Id == songId) > 0)
        {
            _likedCache.Save(cacheSongs);
        }
    }

    /// <summary>
    /// 底部收藏弹窗点击：把「待收藏歌曲」（红心按钮=当前播放，右键菜单=右键所选）收藏到指定自建歌单。
    /// 需登录。收藏不影响红心（收藏 ≠ 喜欢，红心仅由喜欢决定）。
    /// </summary>
    public async Task AddCurrentSongToPlaylistAsync(Playlist playlist)
    {
        var songId = _pendingCollectSongId > 0 ? _pendingCollectSongId : _currentSongId;
        var songText = _pendingCollectSongId > 0 ? _pendingCollectSongText : CurrentSongText;

        if (songId <= 0)
        {
            StatusText = "没有可收藏的歌曲。";
            return;
        }

        if (_isLikeActionInFlight)
        {
            return;
        }

        if (!_loginSession.HasSession)
        {
            StatusText = "请先登录，才能收藏歌曲到歌单。";
            return;
        }

        _isLikeActionInFlight = true;

        try
        {
            await FetchUiAsync(_apiClient.AddSongToPlaylistAsync(playlist.Id, songId));
            StatusText = $"已收藏《{songText}》到歌单《{playlist.DisplayName}》。";
        }
        catch (Exception ex)
        {
            StatusText = GetFriendlyMessage(ex);
        }
        finally
        {
            _isLikeActionInFlight = false;
        }
    }

    /// <summary>
    /// 设置右键菜单「收藏到歌单」的待收藏歌曲，并让弹窗标题显示这首歌。
    /// 调用收藏弹窗后收藏目标是这首歌（而非当前播放歌曲）。
    /// </summary>
    public void SetPendingCollectSong(Song song)
    {
        _pendingCollectSongId = song?.Id ?? 0;
        _pendingCollectSongText = song?.DisplayName ?? string.Empty;
        OnPropertyChanged(nameof(CollectPopupSongText));
    }

    /// <summary>清除待收藏歌曲（收藏弹窗关闭后调用，回到默认收藏当前播放歌曲）。</summary>
    public void ClearPendingCollectSong()
    {
        _pendingCollectSongId = 0;
        _pendingCollectSongText = string.Empty;
        OnPropertyChanged(nameof(CollectPopupSongText));
    }

    /// <summary>
    /// 加载底部收藏弹窗的歌单候选（我创建的歌单，不含特殊歌单）。
    /// 每次打开收藏弹窗都重新拉取，保持歌单列表最新。未登录时提示。
    /// </summary>
    public async Task LoadCreatedPlaylistsForCollectAsync()
    {
        if (!_loginSession.HasSession)
        {
            CollectPlaylistsLoaded = true;
            StatusText = "请先登录，才能收藏歌曲到歌单。";
            return;
        }

        // 旧会话缺 userId 时补齐（与我的歌单/喜欢列表同一逻辑）。
        if (_loginSession.UserId <= 0)
        {
            var restored = await RestoreUserIdAsync();
            if (!restored)
            {
                CollectPlaylistsLoaded = true;
                StatusText = "登录状态失效，请重新登录后再收藏歌曲。";
                return;
            }
        }

        try
        {
            var playlists = await FetchUiAsync(_apiClient.GetUserPlaylistsAsync(_loginSession.UserId));

            CreatedPlaylistsForCollect.Clear();
            foreach (var playlist in playlists)
            {
                if (playlist.Creator?.UserId == _loginSession.UserId)
                {
                    CreatedPlaylistsForCollect.Add(playlist);
                }
            }

            CollectPlaylistsLoaded = true;
            OnPropertyChanged(nameof(HasCollectPlaylists));
            OnPropertyChanged(nameof(ShowCollectEmptyText));

            if (CreatedPlaylistsForCollect.Count == 0)
            {
                StatusText = "你还没有创建的歌单，先创建歌单再收藏歌曲。";
            }
        }
        catch (Exception ex)
        {
            CollectPlaylistsLoaded = true;
            StatusText = GetFriendlyMessage(ex);
        }
    }

    /// <summary>打开收藏窗前复位加载状态，避免复用上次结果导致空提示闪烁。</summary>
    public void ResetCollectPlaylistsState()
    {
        _collectPlaylistsLoaded = false;
        OnPropertyChanged(nameof(CollectPlaylistsLoaded));
        OnPropertyChanged(nameof(ShowCollectEmptyText));
    }

    /// <summary>从 /login/status 补齐 userId 并保存到会话。返回是否成功。</summary>
    private async Task<bool> RestoreUserIdAsync()
    {
        try
        {
            var user = await GetLoginUserAsync();
            if (user == null || user.UserId <= 0)
            {
                return false;
            }

            _loginSession.Save(_loginSession.Cookie, user.UserId, user.Nickname);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task LoadPlaylistTracksAsync(Playlist playlist)
    {
        if (IsBusy)
        {
            return;
        }

        SetBusy(true);

        // 先确定列表上下文并复位标题/搜索/随机索引（不依赖网络，确保进入歌单页立即是干净的列表页）。
        var isOwned = _loginSession.HasSession
            && _loginSession.UserId > 0
            && playlist.Creator?.UserId == _loginSession.UserId;
        if (isOwned)
        {
            _currentPlaylistId = playlist.Id;
            SetListContext(ListContext.CreatedPlaylist);
        }
        else
        {
            SetListContext(ListContext.Other);
        }

        TrackListTitle = playlist.DisplayName;
        ResetSearchKeyword();

        // 仅「我创建的歌单」使用自己的曲目缓存（秒开）；其他歌单不缓存，立即清空列表，
        // 避免残留上一次页面（如「我喜欢的音乐」）的歌曲。
        if (isOwned)
        {
            _playlistCache.Load();
            if (_playlistCache.HasCache(playlist.Id))
            {
                ShowPlaylistSongs(_playlistCache.Get(playlist.Id)!, isOwned);
                StatusText = "正在刷新歌单...";
            }
            else
            {
                Songs.Clear();
                StatusText = "正在加载歌单...";
            }
        }
        else
        {
            Songs.Clear();
            StatusText = "正在加载歌单...";
        }

        try
        {
            var songs = await FetchUiAsync(_apiClient.GetPlaylistTracksAsync(playlist.Id, 50));

            // 增量对齐：已显示的歌曲行保持不动（封面不重载），新增的按服务端顺序插入、消失的移除。
            // 避免整清重建（Clear + 全量 Add）导致的闪烁。
            MergeSongList(Songs, songs);
            SetListContext(isOwned ? ListContext.CreatedPlaylist : ListContext.Other);
            SelectedSong = Songs.FirstOrDefault();
            _currentIndex = -1;
            ResetSearchKeyword();

            // 只缓存自建歌单；收藏/搜索的歌单不写入缓存（避免占用与串用）。
            if (isOwned)
            {
                _playlistCache.Save(playlist.Id, songs);
            }

            StatusText = $"已加载歌单《{playlist.DisplayName}》，共 {songs.Count} 首歌曲，双击歌曲开始播放。";
        }
        catch (Exception ex)
        {
            // 自建歌单刷新失败时保留已显示的缓存，只提示错误。
            if (isOwned && _playlistCache.HasCache(playlist.Id))
            {
                StatusText = $"刷新失败，显示的是上次缓存的歌单。{GetFriendlyMessage(ex)}";
            }
            else
            {
                StatusText = GetFriendlyMessage(ex);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 把歌单曲目填入歌曲列表页。清空旧列表，逐个添加，复位选中/随机索引/搜索词。
    /// </summary>
    private void ShowPlaylistSongs(IReadOnlyList<Song> songs, bool isOwned)
    {
        SetListContext(isOwned ? ListContext.CreatedPlaylist : ListContext.Other);
        Songs.Clear();
        foreach (var song in songs)
        {
            Songs.Add(song);
        }

        SelectedSong = Songs.FirstOrDefault();
        _currentIndex = -1;
        ResetSearchKeyword();
    }

    /// <summary>
    /// 后台增量对齐歌曲列表到目标顺序：
    /// 复用显示集合里已有的 Song 对象（同一对象 → 同一 ImageSource → 封面不重载），
    /// 只对位置/增减做最小改动——已存在的行保持原对象不动，新增的歌单次插入、
    /// 消失的移除、顺序变化的最少搬移。目标内同一首歌重复时只保留首个。
    /// </summary>
    private static void MergeSongList(ObservableCollection<Song> display, IReadOnlyList<Song> target)
    {
        var existingById = display.ToDictionary(s => s.Id, s => s);

        // 按目标顺序去重，已有对象复用（封面不重载），新歌用新对象。
        var finalList = new List<Song>();
        var seen = new HashSet<long>();
        foreach (var song in target)
        {
            if (seen.Add(song.Id))
            {
                finalList.Add(existingById.TryGetValue(song.Id, out var reuse) ? reuse : song);
            }
        }

        var finalIds = new HashSet<long>(finalList.Select(s => s.Id));

        // 1) 移除已不在目标里的行（从后往前避免索引错位）。
        for (var i = display.Count - 1; i >= 0; i--)
        {
            if (!finalIds.Contains(display[i].Id))
            {
                display.RemoveAt(i);
            }
        }

        // 2) 逐项对齐到目标顺序：相同即跳过；目标歌已在显示里但位置不对 → 移到当前位置；
        //    显示里没有 → 直接插入。全程复用已有对象。
        var dispPos = 0;
        for (var fi = 0; fi < finalList.Count; fi++)
        {
            var targetSong = finalList[fi];
            if (dispPos < display.Count && display[dispPos].Id == targetSong.Id)
            {
                dispPos++;
                continue;
            }

            var existingPos = -1;
            for (var j = dispPos; j < display.Count; j++)
            {
                if (display[j].Id == targetSong.Id)
                {
                    existingPos = j;
                    break;
                }
            }

            if (existingPos >= 0)
            {
                // 位置不对：从原位移除再插入到当前目标位置（对象同一，封面不重载）。
                display.RemoveAt(existingPos);
                display.Insert(dispPos, targetSong);
            }
            else
            {
                // 新歌：直接插入。
                display.Insert(dispPos, targetSong);
            }

            dispPos++;
        }
    }

    /// <summary>
    /// 首页第一栏日推歌单卡片（私人雷达/日系/欧美）点击：拉取曲目到歌曲列表页。
    /// 与 LoadPlaylistTracksAsync 共用加载流程，但 status 提示区分「每日更新」。
    /// </summary>
    public async Task LoadDailyPlaylistAsync(HomeQuickCard card)
    {
        if (IsBusy)
        {
            return;
        }

        SetBusy(true);

        try
        {
            // 日推歌单是系统生成的推荐，不缓存：进入前清空列表，避免残留上一页歌曲。
            SetListContext(ListContext.Other);
            Songs.Clear();
            TrackListTitle = card.Title;

            var songs = await FetchUiAsync(_apiClient.GetPlaylistTracksAsync(card.PlaylistId, 50));
            foreach (var song in songs)
            {
                Songs.Add(song);
            }

            SelectedSong = Songs.FirstOrDefault();
            _currentIndex = -1;
            ResetSearchKeyword();
            StatusText = $"已加载《{card.Title}》共 {songs.Count} 首歌曲，双击歌曲开始播放。";
        }
        catch (Exception ex)
        {
            StatusText = GetFriendlyMessage(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>加载专辑歌曲到歌曲列表（不自动播放），并复位搜索状态。</summary>
    public async Task LoadAlbumSongsAsync(Album album)    {
        if (IsBusy)
        {
            return;
        }

        SetBusy(true);

        try
        {
            // 专辑歌曲不缓存：进入前清空列表，避免残留上一页歌曲。
            SetListContext(ListContext.Other);
            Songs.Clear();
            TrackListTitle = album.DisplayName;

            var songs = await FetchUiAsync(_apiClient.GetAlbumTracksAsync(album.Id));
            foreach (var song in songs)
            {
                Songs.Add(song);
            }

            SelectedSong = Songs.FirstOrDefault();
            _currentIndex = -1;
            ResetSearchKeyword();
            StatusText = $"已加载专辑《{album.DisplayName}》，共 {songs.Count} 首歌曲，双击歌曲开始播放。";
        }
        catch (Exception ex)
        {
            StatusText = GetFriendlyMessage(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>加载歌手热门歌曲到歌曲列表（不自动播放），并复位搜索状态。</summary>
    public async Task LoadArtistTopSongsAsync(Artist artist)
    {
        if (IsBusy)
        {
            return;
        }

        SetBusy(true);

        try
        {
            // 歌手热门歌曲不缓存：进入前清空列表，避免残留上一页歌曲。
            SetListContext(ListContext.Other);
            Songs.Clear();
            TrackListTitle = artist.DisplayName;

            var songs = await FetchUiAsync(_apiClient.GetArtistTopSongsAsync(artist.Id));
            foreach (var song in songs)
            {
                Songs.Add(song);
            }

            SelectedSong = Songs.FirstOrDefault();
            _currentIndex = -1;
            ResetSearchKeyword();
            StatusText = $"已加载歌手《{artist.DisplayName}》的热门歌曲，共 {songs.Count} 首，双击歌曲开始播放。";
        }
        catch (Exception ex)
        {
            StatusText = GetFriendlyMessage(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 把歌曲列表填成「非全站搜索结果」时清空搜索关键词并复位类型为歌曲，
    /// 避免搜索框残留旧词误导、以及结果区显示错误的类型列表。
    /// </summary>
    public void ResetSearchKeyword()
    {
        if (!string.IsNullOrEmpty(_searchKeyword))
        {
            _searchKeyword = string.Empty;
            OnPropertyChanged(nameof(SearchKeyword));
        }

        if (_currentSearchType != SearchType.Song)
        {
            SwitchSearchType(SearchType.Song, reSearch: false);
        }
    }

    /// <summary>
    /// 从列表第一首开始播放（播放全部入口）。queue 非空且非 Songs 时物化进 Songs（使播放队列 == 显示列表）。
    /// 播放动作建立固定播放队列：先物化展示列表，再一次性快照进 _playQueue。
    /// 之后上一首 / 下一首 / 自然连播都在 _playQueue 内进行；浏览其他歌单不会动这个队列。
    /// </summary>
    public async Task PlayListAsync(IReadOnlyList<Song>? queue, string statusPrefix)
    {
        if (queue != null && !ReferenceEquals(queue, Songs))
        {
            Songs.Clear();
            foreach (var queuedSong in queue)
            {
                Songs.Add(queuedSong);
            }
        }

        if (Songs.Count == 0)
        {
            StatusText = "没有可播放的歌曲。";
            return;
        }

        _playQueue = new List<Song>(Songs);
        _currentIndex = 0;
        Song song;
        if (PlaybackMode == PlaybackMode.Shuffle)
        {
            // 随机模式点「播放全部」：对播放队列完整洗牌，队列首位也是随机挑的（需求 A2）。
            // 纯洗牌不锚定 → 传 -1，首播歌曲是 _playOrder[0] 这个随机第一首。
            RebuildPlayOrder(-1);
            _currentIndex = _playOrder![0];
            song = _playQueue[_currentIndex];
            StatusText = $"{statusPrefix}，随机播放。";
        }
        else
        {
            song = _playQueue[0];
            StatusText = $"{statusPrefix}，从第一首开始播放。";
        }

        SelectedSong = song;
        await PlaySongAsync(song);
    }

    /// <summary>喜欢的音乐页「播放全部」：有搜索过滤时只播过滤结果。</summary>
    public async Task PlayAllLikedAsync()
    {
        if (Songs.Count == 0)
        {
            StatusText = "喜欢的音乐列表为空，没有可播放的歌曲。";
            return;
        }

        IReadOnlyList<Song>? queue = null;
        if (!string.IsNullOrWhiteSpace(_likedSearchText))
        {
            var filtered = DisplaySongs.Cast<Song>().ToList();
            if (filtered.Count == 0)
            {
                StatusText = "当前搜索结果没有匹配的歌曲。";
                return;
            }

            queue = filtered;
        }

        await PlayListAsync(queue, "正在播放喜欢的音乐");

        // 过滤生效时：物化后清空搜索词，让显示列表 == 播放队列，避免歧义。
        if (queue != null)
        {
            _likedSearchText = string.Empty;
            OnPropertyChanged(nameof(ListFilterText));
            ApplyListFilter();
        }
    }

    /// <summary>选中一首歌（双击 / 悬停播放 / 右键播放前设置 UI 选中态）。播放队列在 PlaySelectedAsync 里快照。</summary>
    public void SelectSong(Song song)
    {
        SelectedSong = song;
    }

    /// <summary>
    /// 播放动作入口：双击 / 悬停播放 / 右键播放 / 播放按钮统一走这里。
    /// 把当前展示列表快照成固定播放队列（_playQueue），并定位到所选歌曲。
    /// 之后上一首 / 下一首 / 自然连播都在这个队列内进行；
    /// 浏览/搜索/点开其他歌单只会改 Songs，不会动这个队列。
    /// 注意：本方法只在「未播放未暂停」时被调用（播放/暂停按钮新建播放），
    /// 恢复播放走 _playbackService.Resume()，不会经过这里。
    /// </summary>
    public async Task PlaySelectedAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var song = SelectedSong ?? Songs.FirstOrDefault();
        if (song == null)
        {
            StatusText = "当前没有可播放的歌曲。";
            return;
        }

        // 播放动作：始终把当前展示列表快照成新队列，锚点 = 本次要播的歌。
        _playQueue = new List<Song>(Songs);
        _currentIndex = _playQueue.IndexOf(song);

        // 随机模式下点击某首歌：以这首歌为锚点重新洗牌，它作为随机队列第一首（需求 A1）。
        if (PlaybackMode == PlaybackMode.Shuffle)
        {
            RebuildPlayOrder(_currentIndex);
        }

        await PlaySongAsync(song);
    }

    public async Task PlayNextAsync()
    {
        var index = PickNextIndex(forward: true);
        if (index < 0)
        {
            return;
        }

        _currentIndex = index;
        var song = _playQueue[index];
        SelectedSong = song;
        await PlaySongAsync(song);
    }

    public async Task PlayPreviousAsync()
    {
        var index = PickNextIndex(forward: false);
        if (index < 0)
        {
            return;
        }

        _currentIndex = index;
        var song = _playQueue[index];
        SelectedSong = song;
        await PlaySongAsync(song);
    }

    /// <summary>
    /// 按当前播放模式返回下一/上一首在播放队列（_playQueue）中的索引。返回 -1 表示无歌可播。
    /// 三个调用点（下一首/上一首/自然播完）共用，保证模式一致。
    /// 队列为空时按当前展示列表（Songs）重建，避免旧版本路径（直接点下一首）崩溃。
    /// </summary>
    private int PickNextIndex(bool forward)
    {
        if (_playQueue.Count == 0)
        {
            _playQueue = new List<Song>(Songs);
        }

        if (_playQueue.Count == 0)
        {
            return -1;
        }

        // 从未播放（_currentIndex == -1）或索引越界（队列被替换后）→ 队列头/尾兜底。
        if (_currentIndex < 0 || _currentIndex >= _playQueue.Count)
        {
            if (PlaybackMode == PlaybackMode.Shuffle)
            {
                EnsurePlayOrder();
                return forward ? _playOrder![0] : _playOrder![^1];
            }

            return forward ? 0 : _playQueue.Count - 1;
        }

        return PlaybackMode switch
        {
            PlaybackMode.RepeatOne => _currentIndex, // 单曲循环：重复当前
            PlaybackMode.Shuffle => ShuffleIndex(forward), // 随机：固定队列内顺序导航
            _ => SequentialIndex(forward), // 顺序：到头循环
        };
    }

    /// <summary>随机模式的队列导航：在固定随机队列里前进/后退（环）。</summary>
    private int ShuffleIndex(bool forward)
    {
        EnsurePlayOrder();

        var order = _playOrder!;
        var pos = Array.IndexOf(order, _currentIndex);
        if (pos < 0)
        {
            // 当前索引不在队列里（理论上不会，防御兜底）→ 以当前歌为锚点重建后从头走。
            RebuildPlayOrder(_currentIndex);
            return forward ? _playOrder![0] : _playOrder![^1];
        }

        var nextPos = forward
            ? (pos + 1) % order.Length
            : (pos - 1 + order.Length) % order.Length;
        return order[nextPos];
    }

    /// <summary>
    /// 确保随机队列已生成：未建时以当前歌为锚点洗牌。防御 select-then-next / 直接 next 的路径。
    /// </summary>
    private void EnsurePlayOrder()
    {
        if (_playOrder == null)
        {
            RebuildPlayOrder(_currentIndex >= 0 ? _currentIndex : 0);
        }
    }

    /// <summary>顺序模式：forward=true 下一首（到尾回 0），否则上一首（到头回尾部）。</summary>
    private int SequentialIndex(bool forward)
    {
        return forward
            ? (_currentIndex < _playQueue.Count - 1 ? _currentIndex + 1 : 0)
            : (_currentIndex > 0 ? _currentIndex - 1 : _playQueue.Count - 1);
    }

    /// <summary>
    /// 重新生成随机模式下的固定播放队列（_playQueue 索引的 Fisher-Yates 随机排列）。
    /// startIndex >= 0 时把该索引交换到队列首位（作为首播歌曲锚点）；
    /// startIndex == -1 时纯洗牌，队列首位也是随机挑的（需求 A2 播放全部）。
    /// 非 Shuffle 模式或播放队列为空时清空队列。
    /// </summary>
    private void RebuildPlayOrder(int startIndex)
    {
        if (PlaybackMode != PlaybackMode.Shuffle || _playQueue.Count == 0)
        {
            _playOrder = null;
            return;
        }

        var order = new int[_playQueue.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        // Fisher-Yates 洗牌。
        for (int i = order.Length - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        // 锚定首播歌曲到队列首位。
        if (startIndex >= 0)
        {
            var anchorPos = Array.IndexOf(order, startIndex);
            if (anchorPos > 0)
            {
                (order[0], order[anchorPos]) = (order[anchorPos], order[0]);
            }
        }

        _playOrder = order;
    }

    public async Task TogglePlayPauseAsync()
    {
        if (IsPaused)
        {
            _playbackService.Resume();
            return;
        }

        if (IsPlaying)
        {
            _playbackService.Pause();
            return;
        }

        await PlaySelectedAsync();
    }

    /// <summary>
    /// 停止后重置进度显示
    /// </summary>
    public void Stop()
    {
        _playbackService.Stop();
        PositionRatio = 0;
        PositionText = "00:00";
        DurationText = "00:00";
        _cachedDuration = TimeSpan.Zero;
        StatusText = "播放已停止";
    }

    /// <summary>拖拽 Thumb 开始或点击轨道 — 暂停定时器更新防止滑块跳动。</summary>
    public void BeginSeek()
    {
        _isSeeking = true;
    }

    /// <summary>拖拽松手或点击完 — 用比例值跳转。</summary>
    public void EndSeek(double ratio)
    {
        _isSeeking = false;
        SeekTo(ratio);
    }

    /// <summary>
    /// 直接 Seek 音频 + 更新进度文本显示。
    /// </summary>
    private void SeekTo(double ratio)
    {
        if (_cachedDuration <= TimeSpan.Zero)
        {
            return;
        }

        double clampedRatio = Math.Clamp(ratio, 0.0, 1.0);
        var targetPosition = _cachedDuration * clampedRatio;

        PositionRatio = clampedRatio;
        PositionText = targetPosition.ToString(@"mm\:ss");

        _playbackService.Seek(targetPosition);
    }

    public void Dispose()
    {
        _playbackService.IsPlayingChanged -= OnIsPlayingChanged;
        _playbackService.IsPausedChanged -= OnIsPausedChanged;
        _playbackService.StatusMessageChanged -= OnStatusMessageChanged;
        _playbackService.ErrorOccurred -= OnPlaybackError;
        _playbackService.PlaybackFinished -= OnPlaybackFinished;
        _playbackService.PositionChanged -= OnPositionChanged;
        _playbackService.Dispose();
        _httpClient.Dispose();
    }

    /// <summary>退出登录。</summary>
    public async Task LogoutAsync()
    {
        try
        {
            await FetchUiAsync(_apiClient.LogoutAsync());
        }
        catch (Exception ex)
        {
            StatusText = GetFriendlyMessage(ex);
        }
        finally
        {
            // 无论服务端登出是否成功，都清除本机会话。
            _loginSession.Clear();
            _apiClient.LoginCookie = string.Empty;
            LoginState.SetLoginStatus(false, string.Empty);
            // 登出后喜欢集合失效，红心图标复位。
            _likedSongIds.Clear();
            CurrentSongLiked = false;
            // 登出后我的歌单列表缓存失效，避免下个账号看到上个人的歌单。
            _myPlaylistCache.Clear();
            CreatedPlaylists.Clear();
            CollectedPlaylists.Clear();
            StatusText = "已退出登录";
        }
    }

    /// <summary>启动时恢复登录态（异步，不阻塞界面）。</summary>
    public async Task InitializeLoginAsync()
    {
        if (!_loginSession.HasSession)
        {
            LoginState.SetLoginStatus(false, string.Empty);
            return;
        }

        try
        {
            var valid = await FetchUiAsync(_apiClient.IsLoggedInAsync());
            if (valid)
            {
                LoginState.SetLoginStatus(true, _loginSession.Nickname);
            }
            else
            {
                // 凭证失效，清掉本地会话。
                _loginSession.Clear();
                _apiClient.LoginCookie = string.Empty;
                LoginState.SetLoginStatus(false, string.Empty);
            }
        }
        catch (Exception)
        {
            // API 暂不可达时先按已保存的会话显示，避免误清用户登录态。
            LoginState.SetLoginStatus(true, _loginSession.Nickname);
        }
    }

    /// <summary>登录成功后保存会话并更新 UI 状态。由 LoginWindow 在 803 时调用。</summary>
    public async Task CompleteLoginAsync(string cookie)
    {
        // 只保存登录凭证 MUSIC_U（cookie 串可能含多个键值）。
        var musicUCookie = cookie.Split(';')
            .Select(part => part.Trim())
            .FirstOrDefault(part => part.StartsWith("MUSIC_U=", StringComparison.OrdinalIgnoreCase));

        var finalCookie = string.IsNullOrWhiteSpace(musicUCookie)
            ? cookie
            : musicUCookie;

        _loginSession.Save(finalCookie, 0, string.Empty);
        _apiClient.LoginCookie = finalCookie;

        var nickname = string.Empty;
        long userId = 0;
        try
        {
            var user = await GetLoginUserAsync();
            nickname = user?.Nickname ?? string.Empty;
            userId = user?.UserId ?? 0;
        }
        catch (Exception)
        {
            // 用户信息获取失败不影响登录成功，下次启动可恢复。
        }

        _loginSession.Save(finalCookie, userId, nickname);
        LoginState.SetLoginStatus(true, nickname);
        StatusText = $"已登录为 {LoginState.Nickname}";

        // 登录成功后后台拉一次喜欢 id 列表，保证红心图标有正确的初始状态。
        if (userId > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var ids = await _apiClient.GetLikedSongIdsAsync(userId).ConfigureAwait(false);
                    await BackToUiAsync();
                    _likedSongIds.Clear();
                    foreach (var id in ids)
                    {
                        _likedSongIds.Add(id);
                    }

                    if (_currentSongId > 0)
                    {
                        CurrentSongLiked = _likedSongIds.Contains(_currentSongId);
                    }
                }
                catch (Exception)
                {
                    // 登录后拉喜欢列表失败不影响登录成功，红心状态维持默认。
                }
            });
        }
    }

    private async Task<LoginUser?> GetLoginUserAsync()
    {
        var response = await FetchUiAsync(_apiClient.GetLoginStatusAsync());
        // data.profile 存在才说明已登录，返回用户昵称。
        return response.Data?.Profile;
    }

    private async Task PlaySongAsync(Song song)
    {
        SetBusy(true);

        // 记录本次请求代次；await 期间若用户又切了歌，旧请求结果不再覆盖 UI。
        var myRequestId = ++_playRequestId;

        try
        {
            // 每次开始新歌曲时重置缓存时长，防止上一首的 cachedDuration 干扰。
            _cachedDuration = TimeSpan.Zero;

            StatusText = $"正在获取《{song.DisplayName}》的音频链接...";
            var urlInfo = await FetchUiAsync(_apiClient.GetPlayableSongUrlAsync(song.Id, PreferredQuality.Key));
            if (!IsCurrentPlayRequest(myRequestId))
            {
                // 期间用户切了别的歌，丢弃本次结果。
                return;
            }

            CurrentSongText = $"{song.DisplayName} - {song.ArtistNames}";
            CurrentSongCoverUrl = song.CoverUrl;
            _currentSongId = song.Id;
            OnPropertyChanged(nameof(CollectPopupSongText));
            // 红心状态：当前播放歌曲是否已在「我喜欢的音乐」里（未登录/未加载喜欢列表时集合为空 → 不红）。
            CurrentSongLiked = _likedSongIds.Contains(song.Id);

            // 状态栏提示实际播放音质。所选音质过高被自动降级时（如母带→高清环绕声），
            // 只提示实际音质，不修改用户偏好（降级只对当前歌曲生效）。
            var formatText = string.Equals(urlInfo.Type, "flac", StringComparison.OrdinalIgnoreCase) ? "FLAC" : "MP3";
            var actualDisplay = GetQualityDisplayName(urlInfo.Level ?? PreferredQuality.Key);
            StatusText = $"正在以 {actualDisplay}（{formatText}）播放《{song.DisplayName}》";
            await _playbackService.PlayAudioAsync(urlInfo.Url!, urlInfo.Type ?? "mp3", song.DisplayName);
        }
        catch (Exception ex)
        {
            if (!IsCurrentPlayRequest(myRequestId))
            {
                return;
            }

            StatusText = GetFriendlyMessage(ex);
        }
        finally
        {
            // 只有最新请求才释放忙碌状态，防止旧请求提前解锁导致 UI 抖动。
            if (IsCurrentPlayRequest(myRequestId))
            {
                SetBusy(false);
            }
        }
    }

    /// <summary>当前播放请求仍是最新的（期间没有发起新的切歌请求）。</summary>
    private bool IsCurrentPlayRequest(int myRequestId) => myRequestId == _playRequestId;

    /// <summary>切换音质后重新加载当前歌曲。</summary>
    private async Task ReloadWithNewQualityAsync()
    {
        if (!IsPlaying && !IsPaused)
        {
            return;
        }

        var song = SelectedSong ?? Songs.FirstOrDefault();
        if (song == null)
        {
            return;
        }

        StatusText = $"已切换到 {PreferredQuality.Display}，重新加载当前歌曲...";
        await PlaySongAsync(song);
    }

    private string GetQualityDisplayName(string level)
    {
        return QualityOptions.FirstOrDefault(o => o.Key == level)?.Display ?? level;
    }

    /// <summary>
    /// 确保后续代码回到 UI 线程（API 内部 ConfigureAwait(false) 会把 continuation 留在线程池）。
    /// 已在 UI 线程时零开销直接返回。
    /// </summary>
    public Task BackToUiAsync()
    {
        var ctx = _uiSyncContext;
        if (ctx == null || SynchronizationContext.Current == ctx)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Post(_ => tcs.SetResult(true), null);
        return tcs.Task;
    }

    /// <summary>取 API 结果后强制回到 UI 线程，供所有加载方法统一使用（避免跨线程操作集合）。</summary>
    private async Task<T> FetchUiAsync<T>(Task<T> task)
    {
        var result = await task.ConfigureAwait(false);
        await BackToUiAsync();
        return result;
    }

    /// <summary>无返回值 API（如登出）的回 UI 线程版本。</summary>
    private async Task FetchUiAsync(Task task)
    {
        await task.ConfigureAwait(false);
        await BackToUiAsync();
    }

    private void SetBusy(bool value)
    {
        IsBusy = value;
    }

    private void OnIsPlayingChanged(object? sender, bool isPlaying)
    {
        IsPlaying = isPlaying;
    }

    private void OnIsPausedChanged(object? sender, bool isPaused)
    {
        IsPaused = isPaused;
    }

    private void OnStatusMessageChanged(object? sender, string message)
    {
        StatusText = message;
    }

    private void OnPlaybackError(object? sender, Exception ex)
    {
        StatusText = GetFriendlyMessage(ex);
    }

    private async void OnPlaybackFinished(object? sender, EventArgs e)
    {
        // 歌曲自然播放完毕 → 按播放模式在固定播放队列里切下一首（浏览其他歌单不动队列）。
        var index = PickNextIndex(forward: true);
        if (index < 0)
        {
            return;
        }

        _currentIndex = index;
        var song = _playQueue[index];
        SelectedSong = song;
        await PlaySongAsync(song);
    }

    private void OnPositionChanged(object? sender, (TimeSpan Position, TimeSpan Duration) e)
    {
        _cachedDuration = e.Duration;

        var position = e.Position;
        var duration = e.Duration;

        // 进度文本
        PositionText = position.ToString(@"mm\:ss");
        DurationText = duration.ToString(@"mm\:ss");

        // 进度比例 —— 用户拖动时不更新 Slider，避免跳动
        if (!_isSeeking)
        {
            var ratio = duration.TotalSeconds > 0
                ? position.TotalSeconds / duration.TotalSeconds
                : 0;
            _positionRatio = Math.Clamp(ratio, 0.0, 1.0);

            // 标记为播放器回写，Slider 的 ValueChanged 据此跳过 Seek。
            IsPositionUpdateFromPlayback = true;
            OnPropertyChanged(nameof(PositionRatio));
            IsPositionUpdateFromPlayback = false;
        }
    }

    private static string GetFriendlyMessage(Exception ex)
    {
        if (ex is COMException { HResult: var hr } && unchecked((uint)hr) == AudioClientDeviceInUse)
        {
            return "音频设备被其他程序占用，请关闭正在使用该设备的程序后重试，或到「设置」切换输出设备。";
        }

        if (ex is NeteaseApiException or TaskCanceledException)
        {
            return ex.Message;
        }

        return $"播放失败：{ex.Message}";
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
