using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExclusiveMusicPlayer.ViewModels;

/// <summary>
/// 登录相关的 UI 状态：登录窗口的二维码与提示文本、主窗口账号卡片的昵称与登录态。
/// </summary>
public sealed class LoginViewModel : INotifyPropertyChanged
{
    private ImageSource? _qrImageSource;
    private string _statusText = "正在获取二维码...";
    private bool _isLoggedIn;
    private string _nickname = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>二维码图片（data URL 转成 BitmapImage）。</summary>
    public ImageSource? QrImageSource
    {
        get => _qrImageSource;
        private set => SetProperty(ref _qrImageSource, value);
    }

    /// <summary>登录窗口的状态提示文本。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>是否已登录。</summary>
    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        private set => SetProperty(ref _isLoggedIn, value);
    }

    public string Nickname
    {
        get => _nickname;
        private set => SetProperty(ref _nickname, value);
    }

    /// <summary>加载二维码前清空图片、显示提示。</summary>
    public Task ShowQrLoadingAsync()
    {
        QrImageSource = null;
        StatusText = "正在获取二维码...";
        return Task.CompletedTask;
    }

    /// <summary>显示二维码图片。dataUrl 形如 data:image/png;base64,xxxx。</summary>
    public Task ShowQrAsync(string dataUrl)
    {
        QrImageSource = DataUrlToImage(dataUrl);
        StatusText = "请使用网易云音乐 App 扫码";
        return Task.CompletedTask;
    }

    public Task ShowStatusAsync(string message)
    {
        StatusText = message;
        return Task.CompletedTask;
    }

    public Task ShowErrorAsync(string message)
    {
        StatusText = message;
        return Task.CompletedTask;
    }

    /// <summary>登录成功后设置主窗口账号卡片状态。</summary>
    public void SetLoginStatus(bool isLoggedIn, string nickname)
    {
        IsLoggedIn = isLoggedIn;
        Nickname = string.IsNullOrWhiteSpace(nickname) ? "网易云用户" : nickname;
        StatusText = isLoggedIn ? "已登录" : "未登录";
    }

    private static BitmapImage? DataUrlToImage(string dataUrl)
    {
        try
        {
            var base64 = dataUrl[(dataUrl.IndexOf(',') + 1)..];
            var bytes = Convert.FromBase64String(base64);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
