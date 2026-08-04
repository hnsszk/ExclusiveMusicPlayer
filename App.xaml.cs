using System.Windows;
using ExclusiveMusicPlayer.Services;

namespace ExclusiveMusicPlayer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 启动流程：先显示加载窗口，等待本地 API 就绪（或跳过/超时放行），
        // 关闭后进入主窗口。退出时终止本服务拉起的本地 API 进程。
        var splash = new SplashWindow();
        var main = new MainWindow(splash);
        // 关键：先把 MainWindow 指定为主窗口，避免 ShutdownMode=OnMainWindowClose 下
        // splash（第一个也是唯一窗口）关闭后应用误判「没有主窗口」而退出。
        MainWindow = main;
        // 注意：必须在 splash.Show() 之前订阅 Closed。开关关闭时 SplashWindow_Loaded
        // 会同步 Close()，若此时未订阅，Closed 事件丢失 → main.Show() 不执行 → 应用退出。
        splash.Closed += (_, _) =>
        {
            // 本地自动拉起成功且端口发生变化时，让播放器连到实际生效端口。
            if (LocalApiService.Instance.LocalApiBaseUrl is { } localUrl
                && main.DataContext is ViewModels.MainViewModel vm)
            {
                vm.ApplyLocalApiBaseUrl(localUrl);
            }

            main.Show();
        };
        main.Closed += (_, _) => LocalApiService.Instance.Stop();

        splash.Show();
    }
}
