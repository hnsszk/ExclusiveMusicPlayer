using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ExclusiveMusicPlayer.Services;

/// <summary>
/// 本地网易云 API 服务的启动/就绪/停止管理。
/// 分发场景：播放器 exe 旁放置 api-server 目录（内含 runtime\node.exe，用户无需安装 Node）。
/// 策略：启动时总是尝试拉起本地 API；已保存公网地址（非 localhost）则跳过。
/// 停止时只终止自己拉起的进程，不杀用户手动开启的服务。
/// </summary>
public sealed class LocalApiService
{
    /// <summary>本地 API 目录名（相对于播放器可执行文件所在目录）。</summary>
    private const string ApiDirName = "api-server";

    /// <summary>本地 API 默认端口。</summary>
    public const int DefaultPort = 3000;

    /// <summary>每次探测就绪的等待间隔。</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>就绪超时：超过则放弃等待（不阻塞进入播放器）。</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

    /// <summary>进程内共享实例（SplashWindow 启动、App 退出时 Stop 共用）。</summary>
    public static LocalApiService Instance { get; } = new();

    private Process? _process;
    private bool _started;

    /// <summary>本地 API 是否已处于可用状态（复用既有服务或本服务拉起成功）。</summary>
    public bool IsLocalApiAvailable { get; private set; }

    /// <summary>当前生效的本地 API 地址（http://127.0.0.1:PORT）。</summary>
    public string? LocalApiBaseUrl { get; private set; }

    /// <summary>
    /// 定位本地 API 目录。分发时优先播放器 exe 旁的同名目录；开发（未发布）时回退到
    /// 项目根目录（仓库内包含 api-enhanced 与 api-dist-test 两种形态）。找不到返回 null。
    /// </summary>
    private static string? FindApiDir()
    {
        // 分发：exe 所在目录下 api-server。
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeDir))
        {
            var candidate = Path.Combine(exeDir, ApiDirName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        // 开发：逐级向上找包含 api-enhanced 的仓库根（bin\Debug\net8.0-windows 等）。
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        for (var d = dir; d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, ApiDirName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 确保本地 API 可用：
    /// 1. 先探测默认端口——已有可用的本地服务则直接复用（不重复拉起）；
    /// 2. 否则用随附 runtime\node.exe 拉起 app.js（默认端口被占时自动换端口）；
    /// 3. 就绪探测：轮询实际生效端口上的 /login/status 直到返回 200（或超时）。
    /// </summary>
    public async Task<string?> EnsureLocalApiAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (await IsServerReadyAsync(DefaultPort, cancellationToken).ConfigureAwait(false))
            {
                IsLocalApiAvailable = true;
                LocalApiBaseUrl = $"http://127.0.0.1:{DefaultPort}";
                return LocalApiBaseUrl;
            }

            var apiDir = FindApiDir();
            var port = apiDir is null ? 0 : StartProcess(apiDir);
            if (port <= 0)
            {
                return null;
            }

            // 等待就绪（轮询实际生效端口）；超时则放弃，返回 null（不阻塞进入播放器）。
            var deadline = DateTime.UtcNow + ReadyTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(ProbeInterval, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                if (await IsServerReadyAsync(port, cancellationToken).ConfigureAwait(false))
                {
                    IsLocalApiAvailable = true;
                    LocalApiBaseUrl = $"http://127.0.0.1:{port}";
                    return LocalApiBaseUrl;
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>探测指定端口上是否为可用的网易云 API 服务（/login/status 返回 code==200）。
    /// 每次用独立短客户端，避免共享客户端 BaseAddress 变更与连接复用造成的状态污染。</summary>
    private static async Task<bool> IsServerReadyAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            client.BaseAddress = new Uri($"http://127.0.0.1:{port}");
            var response = await client.GetAsync("/login/status", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var code = doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("code", out var innerCode)
                    ? innerCode.GetInt32()
                    : -1;
            return code == 200;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 以随附 runtime\node.exe 拉起 app.js（后台、无窗口）。返回实际生效端口，失败返回 0。
    /// 工作目录 = API 目录；默认端口 3000 被占时自动换一个空闲端口并通过 PORT 环境变量传给服务。
    /// </summary>
    private int StartProcess(string apiDir)
    {
        try
        {
            var nodeExe = Path.Combine(apiDir, "runtime", "node.exe");
            if (!File.Exists(nodeExe))
            {
                return 0;
            }

            // 让服务尝试绑定空闲端口：探测 3000 被占（且非 API）时换端口。
            var startInfo = new ProcessStartInfo
            {
                FileName = nodeExe,
                WorkingDirectory = apiDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var port = ResolvePort();
            startInfo.ArgumentList.Add("app.js");
            if (port != DefaultPort)
            {
                startInfo.Environment["PORT"] = port.ToString();
            }

            _process = Process.Start(startInfo);
            if (_process is null)
            {
                return 0;
            }

            _started = true;
            return port;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>确定本地 API 端口：3000 空闲用 3000；被占则找第一个空闲端口（3000..65535）。</summary>
    private int ResolvePort()
    {
        if (IsPortFree(DefaultPort))
        {
            return DefaultPort;
        }

        for (var port = DefaultPort + 1; port <= 65535; port++)
        {
            if (IsPortFree(port))
            {
                return port;
            }
        }

        return DefaultPort;
    }

    private static bool IsPortFree(int port)
    {
        // 用「连接尝试」探测端口是否空闲：空闲时 connect 会立即被拒（没有监听者），
        // 不会像 TcpListener.Start() 那样真的把端口绑住，避免自占端口。
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            client.Connect(System.Net.IPAddress.Loopback, port);
            return false; // 能连上 = 有进程在监听，端口被占。
        }
        catch (System.Net.Sockets.SocketException)
        {
            return true; // 连不上 = 空闲。
        }
        catch (Exception)
        {
            return false; // 其他异常视为被占，保守处理。
        }
    }

    /// <summary>
    /// 停止本服务拉起的 API 进程。仅在确实由本服务启动（_started）时终止，
    /// 避免误杀用户手动开启的本地服务。退出时可调用。
    /// </summary>
    public void Stop()
    {
        if (_started && _process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 忽略：进程可能已自行退出。
            }
        }

        _started = false;
        _process = null;
    }
}
