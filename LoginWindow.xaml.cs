using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;
using ExclusiveMusicPlayer.Services;
using ExclusiveMusicPlayer.ViewModels;

namespace ExclusiveMusicPlayer;

/// <summary>
/// 登录窗口：获取二维码 key → 生成二维码显示 → 轮询扫码状态。
/// 803 授权成功时把 cookie 交回调用方，由 MainViewModel 保存到会话。
/// 关闭窗口或用户取消时停止轮询。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly NeteaseApiClient _apiClient;

    private CancellationTokenSource? _pollCts;

    public LoginWindow(MainViewModel viewModel, NeteaseApiClient apiClient)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _apiClient = apiClient;

        // DataContext 直接设为登录状态对象，窗口里的 QrImageSource/StatusText 绑定才会生效。
        DataContext = viewModel.LoginState;
        Loaded += LoginWindow_Loaded;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CancelPolling();
        base.OnClosing(e);
    }

    private async void LoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _pollCts = new CancellationTokenSource();
        try
        {
            await _viewModel.LoginState.ShowQrLoadingAsync();
            await PollLoginAsync(_pollCts.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户取消或窗口关闭，正常结束。
        }
        catch (Exception ex)
        {
            await _viewModel.LoginState.ShowErrorAsync($"登录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 轮询循环：未扫码(801)/待确认(802) 时持续轮询；
    /// 803 授权成功取出 cookie 通知 VM 保存；800 过期则重新生成二维码。
    /// 注意：服务端 /login/qr/check 在轮询失败时会返回空 body（code 缺省 0），
    /// 必须当作"等待扫码"继续轮询，而不是当异常退出。
    /// </summary>
    private async Task PollLoginAsync(CancellationToken token)
    {
        var key = await _apiClient.GetLoginQrKeyAsync(token);

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var qrImage = await _apiClient.GetLoginQrImageAsync(key, token);
            await _viewModel.LoginState.ShowQrAsync(qrImage);

            while (true)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(1500, token);

                var check = await _apiClient.CheckLoginQrAsync(key, token);
                switch (check.Code)
                {
                    case 801: // 等待扫码
                        await _viewModel.LoginState.ShowStatusAsync("请使用网易云音乐 App 扫码");
                        break;

                    case 802: // 已扫码，待确认
                        await _viewModel.LoginState.ShowStatusAsync("已扫码，请在手机上确认登录");
                        break;

                    case 803: // 授权成功，cookie 在返回里
                        if (string.IsNullOrWhiteSpace(check.Cookie))
                        {
                            await _viewModel.LoginState.ShowErrorAsync("登录成功但未返回凭证，请重试。");
                            return;
                        }

                        await _viewModel.CompleteLoginAsync(check.Cookie);
                        DialogResult = true;
                        return;

                    case 800: // 二维码过期，重新生成
                        break;

                    default: // 其他状态（含服务端异常返回的空 body）继续轮询等待
                        await _viewModel.LoginState.ShowStatusAsync("请使用网易云音乐 App 扫码");
                        break;
                }

                if (check.Code == 800)
                {
                    break;
                }
            }

            // 二维码过期或异常，刷新 key 重来。
            key = await _apiClient.GetLoginQrKeyAsync(token);
        }
    }

    private void CancelPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }
}
