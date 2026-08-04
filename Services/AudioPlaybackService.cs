using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ExclusiveMusicPlayer.Services;

public sealed class AudioPlaybackService : IDisposable
{
    private const uint AudioClientDeviceInUse = 0x8889000A;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly SynchronizationContext? _synchronizationContext;
    private CancellationTokenSource _cancellation = new();
    private MemoryStream? _audioStream;
    private WaveStream? _audioReader;
    private WasapiOut? _wasapiOut;
    /// <summary>
    /// 软件音量控制器。在 WASAPI 独占模式下，设备没有会话音量（Windows 混音器被绕过），
    /// 音量必须由播放链路自己缩放 PCM 采样来实现。播放启动时用它包住解码流，之后调整音量只改它。
    /// </summary>
    private VolumeSampleProvider? _volumeProvider;
    private string? _tempFile;
    private bool _stopRequested;
    private bool _isPausing;
    private bool _isPlaying;
    private bool _isPaused;
    private TimeSpan _resumePosition;
    private bool _disposed;
    private System.Threading.Timer? _progressTimer;
    private float _volume = 1.0f;
    /// <summary>
    /// 播放代次计数器：每次 PlayAudioAsync 递增。异步初始化完成后若发现代次已过期
    /// （期间又发起了新的播放请求），丢弃这次结果，避免旧请求覆盖新播放导致抽搐切歌。
    /// </summary>
    private int _playGeneration;

    /// <summary>
    /// 用户选择的输出设备 id（MMDeviceEnumerator 匹配用）。空 = 跟随系统默认输出设备。
    /// 由设置页（MainViewModel）在切换输出设备时写入，下次初始化播放输出时生效。
    /// </summary>
    public string OutputDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 输出共享模式：true = WASAPI Shared（走 Windows 混音器，与系统声音混音，可同时出声），
    /// false = WASAPI Exclusive（USB 独占，绕过混音器，其他程序静音）。
    /// 由设置页切换并持久化，播放输出初始化时读取。
    /// </summary>
    public bool ShareModeEnabled { get; set; }

    public event EventHandler<bool>? IsPlayingChanged;
    public event EventHandler<bool>? IsPausedChanged;
    public event EventHandler<string>? StatusMessageChanged;
    public event EventHandler<Exception>? ErrorOccurred;
    public event EventHandler? PlaybackFinished;
    /// <summary>
    /// (当前播放位置, 总时长)。由 250ms 定时器每间隔约 250ms 回调一次，抛到 UI 线程触发。
    /// </summary>
    public event EventHandler<(TimeSpan Position, TimeSpan Duration)>? PositionChanged;

    public bool IsPlaying => _isPlaying;
    public bool IsPaused => _isPaused;

    /// <summary>音量，范围 0.0f ~ 1.0f，默认 1.0f。</summary>
    /// <remarks>
    /// 注意：这里绝不调用 _wasapiOut.Volume。NAudio 的 WasapiOut.Volume 实际上
    /// 写的是 Windows 设备主音量（AudioEndpointVolume.MasterVolumeLevelScalar），
    /// 不是应用音量。在 WASAPI 独占模式下该写操作会把系统音量直接改到滑动条位置
    /// （例如满格拖小一点 → 设备音量 0.9），耳机用户会瞬间被震到。音量改用
    /// VolumeSampleProvider 在软件层缩放，与系统音量彻底隔离。
    /// </remarks>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_volumeProvider != null)
            {
                _volumeProvider.Volume = _volume;
            }
        }
    }

    /// <summary>跳转到指定播放位置。播放、暂停、停止状态下均可调用；暂停/停止后 Seek 会更新恢复点。</summary>
    public void Seek(TimeSpan position)
    {
        if (_audioReader == null)
        {
            // 没有活跃的音频流（例如 Stop 后）→ 仅记录目标位置，后续播放时恢复。
            if (position >= TimeSpan.Zero)
            {
                _resumePosition = position;
            }
            return;
        }

        if (position < TimeSpan.Zero || position > _audioReader.TotalTime)
        {
            return;
        }

        _audioReader.CurrentTime = position;
        _resumePosition = position; // 暂停恢复点与当前 Seek 位置同步，防止 Resume() 回退到旧位置。
    }

    public AudioPlaybackService()
    {
        _synchronizationContext = SynchronizationContext.Current;
    }

    public async Task PlayAudioAsync(
        string url,
        string format,
        string title,
        CancellationToken externalCancellationToken = default)
    {
        StopInternal();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
        _stopRequested = false;
        _resumePosition = TimeSpan.Zero;

        // 记录本次播放代次，异步初始化期间若有新请求，代次会递增，结果会被丢弃。
        var myGeneration = ++_playGeneration;

        SetIsPlaying(true);
        SetIsPaused(false);
        RaiseStatus($"正在连接《{title}》...");

        try
        {
            // 优先流式播放：MediaFoundationReader 直接读取 URL（mp3/flac 均支持，
            // 由 Windows 自带解码，无需下载整曲）。网易 CDN 支持 HTTP Range，seek 可用。
            var reader = await Task.Run(() => new MediaFoundationReader(url), _cancellation.Token);
            if (!IsCurrentGeneration(myGeneration))
            {
                // 初始化期间用户又切了歌，丢弃本次结果，避免旧请求覆盖新播放。
                reader.Dispose();
                return;
            }

            if (reader.TotalTime > TimeSpan.Zero)
            {
                _audioReader = reader;
                _audioStream = null;
                StartPlayback(title);
                return;
            }

            // 个别 URL 元数据缺失导致拿不到时长 → 回退到下载后播放，保证进度条/seek 可用。
            reader.Dispose();
            RaiseStatus($"正在下载《{title}》...");
            await DownloadAndPlayAsync(url, format, title, myGeneration);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            RaiseStatus("已停止");
        }
        catch (Exception ex)
        {
            if (!IsCurrentGeneration(myGeneration))
            {
                // 旧请求的失败不覆盖新播放，也不弹错误提示。
                return;
            }

            CleanupPlayback();
            SetIsPlaying(false);
            SetIsPaused(false);
            RaiseError(ex);
        }
    }

    /// <summary>回退路径：整曲下载到内存（MP3）或临时文件（FLAC）后再播放。</summary>
    private async Task DownloadAndPlayAsync(string url, string format, string title, int myGeneration)
    {
        using var response = await HttpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            _cancellation.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var audioBytes = await response.Content.ReadAsByteArrayAsync(_cancellation.Token).ConfigureAwait(false);

        if (!IsCurrentGeneration(myGeneration))
        {
            return;
        }

        var stream = new MemoryStream(audioBytes, writable: false);
        _audioStream = stream;
        _audioReader = CreateReader(stream, format);
        StartPlayback(title);
    }

    /// <summary>用当前 _audioReader 初始化独占输出并开始播放（流式与下载路径共用）。</summary>
    private void StartPlayback(string title)
    {
        // WASAPI Exclusive 下，上一个独占会话释放需要一点时间；快速切歌时
        // 新会话可能遇到设备被占用（0x8889000A），重试一次即可。
        Exception? lastError = null;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(300);
            }

            try
            {
                StartPlaybackOnce(title);
                return;
            }
            catch (COMException ex) when (unchecked((uint)ex.HResult) == AudioClientDeviceInUse)
            {
                lastError = ex;
                // 设备占用，等待旧会话释放后重试。
            }
        }

        CleanupPlayback();
        SetIsPlaying(false);
        SetIsPaused(false);
        throw lastError ?? new InvalidOperationException("无法启动独占播放。");
    }

    private void StartPlaybackOnce(string title)
    {
        var outputDevice = ResolveOutputDevice();
        var shareMode = ShareModeEnabled ? AudioClientShareMode.Shared : AudioClientShareMode.Exclusive;

        // 核心链路：WasapiOut + 所选输出设备/共享模式。独占模式实现 USB 独占输出；
        // 共享模式走 Windows 混音器（NAudio 内部用设备 MixFormat，PCM 自动转换，无需手动重采样）。
        var wasapi = new WasapiOut(outputDevice, shareMode, true, 20);
        wasapi.PlaybackStopped += OnPlaybackStopped;
        wasapi.Init(BuildOutputProvider(_audioReader));
        wasapi.Play();

        // Init/Play 成功后才挂到字段上；失败时由调用方重试，避免残留坏实例。
        _wasapiOut = wasapi;
        StartProgressTimer();

        RaiseStatus(shareMode == AudioClientShareMode.Exclusive
            ? $"正在以 WASAPI 独占模式播放《{title}》"
            : $"正在以共享模式播放《{title}》");
    }

    /// <summary>
    /// 组装输出链路：解码流 → 软件音量缩放（VolumeSampleProvider）→ WasapiOut。
    /// 独占模式下音量缩放必须发生在软件层（见 Volume 属性备注）；WaveToSampleProvider
    /// 负责把 WaveStream 转成 ISampleProvider，SampleToWaveProvider 再把缩放结果
    /// 转回 IWaveProvider 交给 WasapiOut.Init。包装层都是透传，不持有所属流的生命周期。
    /// </summary>
    private IWaveProvider BuildOutputProvider(WaveStream? reader)
    {
        var provider = reader.ToSampleProvider();
        _volumeProvider = new VolumeSampleProvider(provider) { Volume = _volume };
        return _volumeProvider.ToWaveProvider();
    }

    /// <summary>
    /// 解析本次播放用的输出设备：用户选了具体设备 → 按 id 匹配 MMDevice；
    /// 未选（跟随系统默认）→ 用系统默认渲染端点。设备被拔出等匹配失败时回退系统默认。
    /// </summary>
    private MMDevice ResolveOutputDevice()
    {
        using var deviceEnumerator = new MMDeviceEnumerator();

        if (!string.IsNullOrWhiteSpace(OutputDeviceId))
        {
            foreach (var device in deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (string.Equals(device.ID, OutputDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return device;
                }
            }
        }

        return deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    /// <summary>
    /// 根据格式创建解码器：mp3 用 Mp3FileReader（内存流），
    /// flac 用 MediaFoundationReader（Windows 自带 FLAC 解码，仅支持文件路径，
    /// 因此把已下载的字节写入临时文件再交给它）。
    /// </summary>
    private WaveStream CreateReader(Stream stream, string format)
    {
        if (!string.Equals(format, "flac", StringComparison.OrdinalIgnoreCase))
        {
            return new Mp3FileReader(stream);
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"np_{Guid.NewGuid():N}.flac");
        using (var fileStream = File.Create(tempFile))
        {
            stream.Position = 0;
            stream.CopyTo(fileStream);
        }
        _tempFile = tempFile;
        return new MediaFoundationReader(tempFile);
    }

    public void Pause()
    {
        if (_wasapiOut == null || !_isPlaying || _isPaused)
        {
            return;
        }

        // 暂停时记录当前精确位置作为恢复点。
        // 如果之前用户拖过进度条（Seek 已同步 _resumePosition），这里用 CurrentTime 覆盖也可保证一致。
        _resumePosition = _audioReader?.CurrentTime ?? TimeSpan.Zero;
        _stopRequested = true;
        _isPausing = true;

        try
        {
            _wasapiOut.Stop();
        }
        catch
        {
            // Stop 的异常不阻止后续资源释放，继续按暂停状态处理。
        }

        CleanupWasapi();
        _isPausing = false;
        SetIsPaused(true);
        RaiseStatus($"已暂停于 {_resumePosition.ToString(@"mm\:ss")}");
    }

    public void Resume()
    {
        if (_wasapiOut != null || !_isPaused || _audioReader == null)
        {
            return;
        }

        try
        {
            var outputDevice = ResolveOutputDevice();
            var shareMode = ShareModeEnabled ? AudioClientShareMode.Shared : AudioClientShareMode.Exclusive;

            var wasapi = new WasapiOut(outputDevice, shareMode, true, 20);
            _wasapiOut = wasapi;
            wasapi.PlaybackStopped += OnPlaybackStopped;
            wasapi.Init(BuildOutputProvider(_audioReader));

            // 如果有 Seek 过的恢复位置（包括暂停后拖进度条），从这里恢复；否则从头播。
            if (_resumePosition > TimeSpan.Zero)
            {
                _audioReader.CurrentTime = _resumePosition;
            }

            wasapi.Play();
            StartProgressTimer();
            _stopRequested = false;
            SetIsPaused(false);
            RaiseStatus("继续播放");
        }
        catch (Exception ex)
        {
            CleanupPlayback();
            SetIsPlaying(false);
            SetIsPaused(false);
            RaiseError(ex);
        }
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        StopInternal();
        RaiseStatus("已停止");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopInternal();
    }

    private void StopInternal()
    {
        _stopRequested = true;
        _cancellation.Cancel();
        StopProgressTimer();

        try
        {
            _wasapiOut?.Stop();
        }
        catch
        {
            // 停止尚未完全初始化的设备时，统一交给 CleanupPlayback 释放。
        }

        // 停止前保存当前播放位置供下次继续使用。
        _resumePosition = _audioReader?.CurrentTime ?? _resumePosition;

        CleanupPlayback();
        SetIsPlaying(false);
        SetIsPaused(false);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // 关键：忽略来自旧播放实例的回调。切歌时旧 WasapiOut 的 PlaybackStopped
        // 会异步晚到，此时 _stopRequested 已被新播放重置、_isPlaying 为 true，
        // 若不检查实例，晚到的回调会被误判为"自然播完"并触发 PlaybackFinished，
        // 导致列表从当前歌曲开始无限自动切歌。
        if (!ReferenceEquals(sender, _wasapiOut))
        {
            return;
        }

        if (_isPausing)
        {
            return;
        }

        var naturallyFinished = !_stopRequested && e.Exception == null;

        if (naturallyFinished && _isPlaying)
        {
            // 抛到 UI 线程触发，避免 VM 在音频回调线程操作绑定属性。
            Post(() => PlaybackFinished?.Invoke(this, EventArgs.Empty));
        }

        CleanupPlayback();
        SetIsPlaying(false);
        SetIsPaused(false);

        if (e.Exception != null)
        {
            RaiseError(e.Exception);
        }
    }

    private void CleanupPlayback()
    {
        CleanupWasapi();

        _audioReader?.Dispose();
        _audioStream?.Dispose();
        _audioReader = null;
        _audioStream = null;

        // 删除 FLAC 解码用的临时文件。
        if (_tempFile != null)
        {
            try
            {
                File.Delete(_tempFile);
            }
            catch
            {
                // 临时文件删除失败不阻塞播放流程。
            }

            _tempFile = null;
        }
    }

    private void CleanupWasapi()
    {
        var wasapi = _wasapiOut;
        _wasapiOut = null;
        // 独占输出销毁后，旧的 VolumeSampleProvider 指向已被释放的解码流，必须一并丢弃，
        // 否则暂停/切歌后调整音量会写进已失效的 provider。
        _volumeProvider = null;

        if (wasapi != null)
        {
            wasapi.PlaybackStopped -= OnPlaybackStopped;

            try
            {
                wasapi.Dispose();
            }
            catch
            {
                // 设备拔出等情况不阻塞后续资源释放。
            }
        }
    }

    private void SetIsPlaying(bool value)
    {
        if (_isPlaying == value)
        {
            return;
        }

        _isPlaying = value;
        Post(() => IsPlayingChanged?.Invoke(this, value));
    }

    /// <summary>当前播放请求仍是最新的（期间没有新的播放请求发起）。</summary>
    private bool IsCurrentGeneration(int myGeneration) => myGeneration == _playGeneration;

    private void SetIsPaused(bool value)
    {
        if (_isPaused == value)
        {
            return;
        }

        _isPaused = value;
        Post(() => IsPausedChanged?.Invoke(this, value));
    }

    private void RaiseStatus(string message)
    {
        Post(() => StatusMessageChanged?.Invoke(this, message));
    }

    private void RaiseError(Exception ex)
    {
        Post(() => ErrorOccurred?.Invoke(this, ex));
    }

    private void Post(Action action)
    {
        if (_synchronizationContext != null)
        {
            _synchronizationContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }

    /// <summary>启动进度轮询 Timer，约每 250ms 回调一次 PositionChanged。</summary>
    private void StartProgressTimer()
    {
        StopProgressTimer();
        _progressTimer = new System.Threading.Timer(
            OnProgressTimerTick,
            null,
            dueTime: 0,
            period: 250);
    }

    private void StopProgressTimer()
    {
        var timer = _progressTimer;
        _progressTimer = null;
        timer?.Dispose();
    }

    private void OnProgressTimerTick(object? state)
    {
        var reader = _audioReader;
        if (reader == null)
        {
            return;
        }

        try
        {
            var position = reader.CurrentTime;
            var duration = reader.TotalTime;
            Post(() => PositionChanged?.Invoke(this, (position, duration)));
        }
        catch
        {
            // 音频流关闭时读取位置可能抛异常，忽略即可。
        }
    }
}
