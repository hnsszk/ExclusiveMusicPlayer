using System.Windows;
using ExclusiveMusicPlayer.Services;
using ExclusiveMusicPlayer.ViewModels;

namespace ExclusiveMusicPlayer;

/// <summary>
/// 启动加载窗口：在本地 API 就绪前显示转圈加载提示。
/// 三种结果：本地就绪、跳过（已用公网地址）、超时放行。都会关闭本窗口并进入主界面，
/// 超时/失败只做状态栏提示，不无限等待。
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>本地 API 是否已就绪（供主窗口决定是否提示）。</summary>
    public bool LocalApiReady { get; private set; }

    /// <summary>本地 API 启动是否失败（超时/目录缺失）。供主窗口提示去设置。</summary>
    public bool LocalApiFailed { get; private set; }

    public SplashWindow()
    {
        InitializeComponent();
        Loaded += SplashWindow_Loaded;
    }

    private async void SplashWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var session = new LoginSession();
        session.Load();

        // 设置里关闭了「自动启动本地 API」，或已保存公网地址（非 localhost）：
        // 都不拉起本地 API，直接跳过加载进入主界面。
        if (!session.AutoStartLocalApi || !MainViewModel.IsLocalHostUrl(session.ApiBaseUrl))
        {
            Close();
            return;
        }

        var result = await Task.Run(() => LocalApiService.Instance.EnsureLocalApiAsync()).ConfigureAwait(true);
        LocalApiReady = result is not null;
        LocalApiFailed = result is null;
        Close();
    }
}
