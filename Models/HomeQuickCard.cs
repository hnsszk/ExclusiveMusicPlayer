using System.ComponentModel;

namespace ExclusiveMusicPlayer.Models;

/// <summary>
/// 首页第一栏快捷卡片。是数据驱动模型（而非静态 XAML 按钮），
/// 卡片集合由 ViewModel 统一填充，UI 用 ItemsControl + UniformGrid 等宽排列。
/// </summary>
public sealed class HomeQuickCard : INotifyPropertyChanged
{
    private string _coverUrl = string.Empty;
    private string _subtitle = string.Empty;

    /// <summary>卡片唯一标识（点按后决定加载什么）。</summary>
    public HomeQuickCardKind Kind { get; init; }

    /// <summary>卡片主标题。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>卡片副标题。</summary>
    public string Subtitle
    {
        get => _subtitle;
        set
        {
            if (_subtitle != value)
            {
                _subtitle = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
            }
        }
    }

    /// <summary>卡片封面 URL（空时透出占位色）。</summary>
    public string CoverUrl
    {
        get => _coverUrl;
        set
        {
            if (_coverUrl != value)
            {
                _coverUrl = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverUrl)));
            }
        }
    }

    /// <summary>日推歌单（雷达等）的 id；点按后用它加载曲目。</summary>
    public long PlaylistId { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>首页快捷卡片的加载类型。</summary>
public enum HomeQuickCardKind
{
    /// <summary>我喜欢的音乐：切到喜欢的音乐页。</summary>
    Liked,

    /// <summary>今日推荐：加载日推到歌曲列表页。</summary>
    Daily,

    /// <summary>日推歌单（私人雷达等）：按 PlaylistId 加载曲目到歌曲列表页。</summary>
    DailyPlaylist,
}
