using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ExclusiveMusicPlayer.Models;
using ExclusiveMusicPlayer.Services;
using ExclusiveMusicPlayer.ViewModels;

namespace ExclusiveMusicPlayer;

public partial class MainWindow : Window
{
    // 导航索引：首页 / 搜索 / 喜欢的音乐 / 我的歌单 / 歌曲列表（隐藏项）/ 设置
    private const int HomeIndex = 0;
    private const int SearchIndex = 1;
    private const int LikedIndex = 2;
    private const int MyPlaylistIndex = 3;
    private const int TrackListIndex = 4;
    private const int SettingsIndex = 5;

    private readonly MainViewModel _viewModel = new();

    /// <summary>歌曲列表页的返回目标：进入歌单前所在的导航页（首页/搜索/我的歌单）。</summary>
    private int _trackListReturnIndex = HomeIndex;

    public MainWindow(SplashWindow? splash = null)
    {
        _splash = splash;
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += (_, e) =>
        {
            // 搜索类型变化时刷新结果列表可见性。
            if (e.PropertyName == nameof(MainViewModel.CurrentSearchType))
            {
                UpdateView();
            }
        };
        NavigationList.SelectedIndex = HomeIndex;
        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
    }

    private readonly SplashWindow? _splash;

    /// <summary>窗口最大化状态变化时切换最大化/还原按钮图标，并处理圆角。</summary>
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeIcon is null)
        {
            return;
        }

        var isMaximized = WindowState == WindowState.Maximized;
        // E922=最大化，E923=还原。
        MaximizeIcon.Text = isMaximized ? "" : "";
        MaximizeButton.ToolTip = isMaximized ? "还原" : "最大化";

        // 最大化时去掉圆角，避免透明窗口最大化溢出屏幕；还原时恢复圆角。
        if (RootChrome is not null)
        {
            RootChrome.CornerRadius = isMaximized ? new CornerRadius(0) : new CornerRadius(10);
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>最大化 / 还原切换。</summary>
    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>顶部拖动区：左键按下拖动窗口，双击最大化/还原。</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (WindowState == WindowState.Normal)
        {
            DragMove();
        }
    }

    /// <summary>
    /// 窗口顶部整行拖动：挂在根 RootChrome 上，点击顶部区域（约 90px 内）的非交互控件时拖动窗口。
    /// 覆盖左侧菜单顶部 + 白色内容区顶部（含 header 移到按钮行下方后空出的整条空白带，
    /// 一直到窗口最顶端）。
    /// 搜索框/按钮/列表等交互控件不触发；列表区域（下方）不触发拖动。
    /// </summary>
    private void WindowTopArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 收藏窗打开时是模态遮罩：顶部区域不拖动窗口，点击遮罩由弹窗自己处理关闭。
        if (CollectDialogOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        if (WindowState != WindowState.Normal)
        {
            return;
        }

        // 只在顶部区域（标题行高度内）触发拖动，避免影响列表等下方内容。
        var clickY = e.GetPosition(this).Y;
        if (clickY > 90)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        // 交互控件上不拖动：文本输入、按钮、下拉、滚动条、滑块、列表项等。
        // 注意：按钮/搜索框的内部元素（图标 TextBlock 等）可能作为 OriginalSource，
        // 需向上遍历视觉树找到真正的交互控件。
        if (IsOrInsideInteractive(source))
        {
            return;
        }

        // 双击最大化。
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        DragMove();
        e.Handled = true;
    }

    /// <summary>判断点击元素本身或其祖先是否为交互控件（这些控件上不触发窗口拖动）。</summary>
    private static bool IsOrInsideInteractive(DependencyObject? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is TextBoxBase or Button or ComboBox or Slider or ScrollBar or ListBoxItem or Thumb)
            {
                return true;
            }

            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : null;
        }

        return false;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeLoginAsync();

        // 本地 API 未就绪（未自动拉起/超时失败）：后台补一次拉起并提示，避免空等。
        // 设置了「关闭自动启动本地 API」时不补拉起，直接由 VerifyApiConnectionAsync 提示。
        if (_viewModel.AutoStartLocalApi && (_splash is null || (!_splash.LocalApiReady && _splash.LocalApiFailed)))
        {
            _ = Task.Run(async () =>
            {
                var url = await LocalApiService.Instance.EnsureLocalApiAsync();
                await _viewModel.BackToUiAsync();
                if (url is not null)
                {
                    _viewModel.ApplyLocalApiBaseUrl(url);
                }
                else if (_splash is not null)
                {
                    _viewModel.StatusText = "本地 API 启动失败，请到「设置」检查或改用远程地址。";
                }
            });
        }

        UpdateAccountButton();
        // 启动默认进首页。
        await _viewModel.LoadHomeAsync();
        // 后台验证当前 API 地址是否可用：不可连时状态栏提示去「设置」修改。
        _ = _viewModel.VerifyApiConnectionAsync();
        // 枚举当前输出设备并恢复上次选择（设置页下拉 + 侧栏指示 + 播放输出）。
        _viewModel.RefreshOutputDevices();
    }

    private async void NavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HomeView is null || SearchHeader is null || LikedHeader is null
            || MyPlaylistHeader is null || SongList is null || MyPlaylistView is null)
        {
            return;
        }

        var i = NavigationList.SelectedIndex;
        UpdateView();

        // 点击「喜欢的音乐」时加载列表；点击「我的歌单」时加载歌单。
        if (i == LikedIndex)
        {
            await _viewModel.LoadLikedSongsAsync();
        }
        else if (i == MyPlaylistIndex)
        {
            await _viewModel.LoadMyPlaylistsAsync();
        }
        else if (i == SettingsIndex)
        {
            // 进设置页时重新枚举输出设备，保证下拉列表与当前实际设备一致。
            _viewModel.RefreshOutputDevices();
        }
        else
        {
            // 离开「喜欢的音乐」页时清空列表内搜索词，避免过滤残留影响其他页。
            _viewModel.ClearListFilter();
        }
    }

    /// <summary>按导航 index + 当前搜索类型切换各视图/列表可见性。</summary>
    private void UpdateView()
    {
        if (HomeView is null || SearchHeader is null || LikedHeader is null
            || MyPlaylistHeader is null || TrackListHeader is null || SongListContainer is null
            || PlaylistList is null || AlbumList is null || ArtistList is null || MyPlaylistView is null
            || SettingsView is null || PlayAllSongsButton is null)
        {
            return;
        }

        var i = NavigationList.SelectedIndex;
        var type = _viewModel.CurrentSearchType;
        var onSearch = i == SearchIndex;
        var onLiked = i == LikedIndex;
        var onTrackList = i == TrackListIndex;

        HomeView.Visibility = i == HomeIndex ? Visibility.Visible : Visibility.Collapsed;
        SearchHeader.Visibility = onSearch ? Visibility.Visible : Visibility.Collapsed;
        LikedHeader.Visibility = onLiked ? Visibility.Visible : Visibility.Collapsed;
        MyPlaylistHeader.Visibility = i == MyPlaylistIndex ? Visibility.Visible : Visibility.Collapsed;
        TrackListHeader.Visibility = onTrackList ? Visibility.Visible : Visibility.Collapsed;
        MyPlaylistView.Visibility = i == MyPlaylistIndex ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = i == SettingsIndex ? Visibility.Visible : Visibility.Collapsed;

        // 歌曲列表容器（含「在列表中搜索」条）在「搜索(歌曲类型)」「喜欢的音乐」「歌曲列表」下显示。
        SongListContainer.Visibility = (onSearch && type == SearchType.Song) || onLiked || onTrackList
            ? Visibility.Visible : Visibility.Collapsed;
        PlaylistList.Visibility = onSearch && type == SearchType.Playlist ? Visibility.Visible : Visibility.Collapsed;
        AlbumList.Visibility = onSearch && type == SearchType.Album ? Visibility.Visible : Visibility.Collapsed;
        ArtistList.Visibility = onSearch && type == SearchType.Artist ? Visibility.Visible : Visibility.Collapsed;

        // 「播放全部」只在搜索歌曲类型时显示（全站搜索页列表内搜索条已移除）。
        var showSongControls = onSearch && type == SearchType.Song;
        PlayAllSongsButton.Visibility = showSongControls ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var loginWindow = new LoginWindow(_viewModel, _viewModel.ApiClient)
        {
            Owner = this,
        };
        loginWindow.ShowDialog();
        UpdateAccountButton();

        // 登录后回首页刷新日推封面与喜欢封面。
        if (_viewModel.LoginState.IsLoggedIn && NavigationList.SelectedIndex == HomeIndex)
        {
            await _viewModel.LoadDailyPreviewAsync();
            _viewModel.RefreshLikedCoverFromCache();
        }
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.LogoutAsync();
        _viewModel.ClearHomeDailyData();
        UpdateAccountButton();
    }

    /// <summary>登录态变化后刷新账号卡片：未登录显示「登录」，已登录显示「注销」。</summary>
    private void UpdateAccountButton()
    {
        var isLoggedIn = _viewModel.LoginState.IsLoggedIn;
        LoginButton.Visibility = isLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        LogoutButton.Visibility = isLoggedIn ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>搜索页搜索框回车：执行搜索（已在该页，不跳转）。</summary>
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await _viewModel.SearchAsync();
            e.Handled = true;
        }
    }

    /// <summary>返回按钮：回到首页。</summary>
    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        NavigationList.SelectedIndex = HomeIndex;
    }

    /// <summary>跳到歌曲列表页（点进歌单），记住来源页供返回。</summary>
    private void ShowTrackList(int returnIndex)
    {
        _trackListReturnIndex = returnIndex;
        NavigationList.SelectedIndex = TrackListIndex;
    }

    /// <summary>歌曲列表页返回按钮：回到进入歌单前所在的页面。</summary>
    private void TrackBackButton_Click(object sender, RoutedEventArgs e)
    {
        NavigationList.SelectedIndex = _trackListReturnIndex;
    }

    private async void SongList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SongList.SelectedItem is Song song)
        {
            _viewModel.SelectSong(song);
            await _viewModel.PlaySelectedAsync();
        }
    }

    /// <summary>右键菜单当前指向的歌曲（菜单挂在 ListBox 上，共享一份，靠 ContextMenuOpening 设置）。</summary>
    private Song? _contextMenuSong;

    /// <summary>
    /// 右键打开歌曲菜单时：根据鼠标位置找到被右键的歌曲行，存为菜单目标。
    /// 未点中任何歌曲行（空白处右键）时不打开菜单。
    /// </summary>
    private void SongList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _contextMenuSong = null;

        // 若右键的原始元素本身属于某个 ListBoxItem，取它的数据作为目标歌曲。
        if (e.OriginalSource is DependencyObject source)
        {
            var item = FindAncestor<ListBoxItem>(source);
            _contextMenuSong = item?.DataContext as Song;
        }

        // 空白处右键：不弹出菜单。
        if (_contextMenuSong is null)
        {
            e.Handled = true;
        }
    }

    /// <summary>沿可视树向上找指定类型的祖先元素。</summary>
    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>右键菜单「播放」：播放被右键的歌曲。</summary>
    private async void SongContextMenu_Play_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuSong is Song song)
        {
            _viewModel.SelectSong(song);
            await _viewModel.PlaySelectedAsync();
        }
    }

    /// <summary>右键菜单「收藏到歌单」：以右键所选歌曲为收藏目标打开居中收藏窗。</summary>
    private async void SongContextMenu_Collect_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuSong is Song song)
        {
            _viewModel.SetPendingCollectSong(song);
            await OpenCollectDialogAsync();
        }
    }

    /// <summary>右键菜单「删除」：从当前列表（喜欢的音乐/自建歌单）删除该歌曲。</summary>
    private async void SongContextMenu_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuSong is Song song)
        {
            await _viewModel.RemoveCurrentListSongAsync(song);
        }
    }

    /// <summary>歌曲行悬停播放三角：单击即播放该曲（不改变行的选中逻辑，避免误触）。</summary>
    private async void SongRowPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: Song song })
        {
            _viewModel.SelectSong(song);
            await _viewModel.PlaySelectedAsync();
        }
    }

    private async void PlaylistList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistList.SelectedItem is Playlist playlist)
        {
            await _viewModel.LoadPlaylistTracksAsync(playlist);
            ShowTrackList(SearchIndex);
        }
    }

    /// <summary>「我的歌单」我创建的：单击选中歌单即加载曲目并进入歌曲列表页。</summary>
    private async void CreatedPlaylists_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox { SelectedItem: Playlist playlist })
        {
            await _viewModel.LoadPlaylistTracksAsync(playlist);
            ShowTrackList(MyPlaylistIndex);
        }
    }

    /// <summary>「我的歌单」我收藏的：单击选中歌单即加载曲目并进入歌曲列表页。</summary>
    private async void CollectedPlaylists_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox { SelectedItem: Playlist playlist })
        {
            await _viewModel.LoadPlaylistTracksAsync(playlist);
            ShowTrackList(MyPlaylistIndex);
        }
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.TogglePlayPauseAsync();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Stop();
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.PlayPreviousAsync();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.PlayNextAsync();
    }

    private void CyclePlaybackMode_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CyclePlaybackMode();
    }

    /// <summary>红心按钮：喜欢/取消喜欢当前歌曲（收藏到「我喜欢的音乐」）。</summary>
    private async void ToggleLiked_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ToggleLikedAsync();
    }

    /// <summary>
    /// 收藏按钮：打开居中的收藏歌单选择窗，收藏目标是当前播放歌曲。
    /// 未登录/加载失败时窗口内显示对应提示，不另开弹窗。
    /// </summary>
    private async void CollectButton_Click(object sender, RoutedEventArgs e)
    {
        // 底部按钮默认收藏当前播放歌曲；清除右键设置的待收藏歌曲。
        _viewModel.ClearPendingCollectSong();
        await OpenCollectDialogAsync();
    }

    /// <summary>打开居中收藏窗：复位加载状态并拉取我创建的歌单。</summary>
    private async Task OpenCollectDialogAsync()
    {
        _viewModel.ResetCollectPlaylistsState();
        CollectDialogOverlay.Visibility = Visibility.Visible;
        await _viewModel.LoadCreatedPlaylistsForCollectAsync();
    }

    /// <summary>点击居中收藏窗外的遮罩：关闭收藏窗（仅当点中遮罩本身，卡片内不关闭）。</summary>
    private void CollectDialogOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CollectDialogCard is null
            || e.OriginalSource is DependencyObject origin
            && CollectDialogCard.IsAncestorOf(origin))
        {
            return;
        }

        CloseCollectDialog();
    }

    /// <summary>居中收藏窗右上角「✕」：关闭收藏窗。</summary>
    private void CloseCollectDialog_Click(object sender, RoutedEventArgs e)
    {
        CloseCollectDialog();
    }

    private void CloseCollectDialog()
    {
        CollectDialogOverlay.Visibility = Visibility.Collapsed;
        _viewModel.CreatedPlaylistsForCollect.Clear();
        _viewModel.ClearPendingCollectSong();
    }

    /// <summary>在居中收藏窗里点选一个歌单：收藏目标歌曲，然后关闭窗口。</summary>
    private async void CollectDialogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CollectDialogList.SelectedItem is not Playlist playlist)
        {
            return;
        }

        CollectDialogList.SelectedItem = null; // 复位选择，下次点同一歌单仍可触发
        // 先收藏再关闭：关闭会清空待收藏歌曲（右键目标），收藏需要用到它。
        await _viewModel.AddCurrentSongToPlaylistAsync(playlist);
        CloseCollectDialog();
    }

    private async void PlayAllLiked_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.PlayAllLikedAsync();
    }

    /// <summary>歌曲列表页「播放全部」：播放当前 Songs 队列（搜索结果/歌单曲目/喜欢列表通用）。</summary>
    private async void PlayAllSongs_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.PlayListAsync(null, "正在播放全部歌曲");
    }

    /// <summary>首页搜索框回车：把首页关键词带到搜索页并搜索。</summary>
    private async void HomeSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // 首页搜索框绑 HomeSearchText，搜索页绑 SearchKeyword——带词跳转。
            _viewModel.SearchKeyword = _viewModel.HomeSearchText;
            NavigationList.SelectedIndex = SearchIndex;
            await _viewModel.SearchAsync();
            e.Handled = true;
        }
    }

    /// <summary>首页第一栏快捷卡片点击：按卡片类型分发（喜欢/日推/雷达歌单）。</summary>
    private async void HomeQuickCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HomeQuickCard card })
        {
            return;
        }

        switch (card.Kind)
        {
            case HomeQuickCardKind.Liked:
                // 切到喜欢列表页（SelectionChanged 会触发加载）。
                NavigationList.SelectedIndex = LikedIndex;
                break;

            case HomeQuickCardKind.Daily:
                await _viewModel.LoadDailySongsToQueueAsync();
                // 未登录/加载失败时 Songs 为空，停在首页；成功才切页。
                if (_viewModel.Songs.Count > 0)
                {
                    ShowTrackList(HomeIndex);
                }

                break;

            case HomeQuickCardKind.DailyPlaylist:
                await _viewModel.LoadDailyPlaylistAsync(card);
                ShowTrackList(HomeIndex);
                break;
        }
    }

    /// <summary>首页推荐歌单小卡片：加载曲目并跳到歌曲列表页。</summary>
    private async void RecommendedPlaylistCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Playlist playlist })
        {
            await _viewModel.LoadPlaylistTracksAsync(playlist);
            ShowTrackList(HomeIndex);
        }
    }

    /// <summary>首页推荐歌单区右侧刷新按钮：重新拉取推荐歌单。</summary>
    private async void RefreshRecommended_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshRecommendedPlaylistsAsync();
    }

    /// <summary>设置页「测试连接」：只探测输入框里的地址是否可连，不切换、不保存。</summary>
    private async void TestApiConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.TestApiBaseUrlAsync();
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"测试连接失败：{ex.Message}";
        }
    }

    /// <summary>设置页「保存」：把输入框里的合法地址应用到当前客户端并持久化。不探测、不验证连通性。</summary>
    private async void SaveApiBaseUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.SaveApiBaseUrlAsync();
        }
        catch (Exception ex)
        {
            // 防御：任何意外异常都不应导致应用崩溃。
            _viewModel.StatusText = $"保存 API 地址失败：{ex.Message}";
        }
    }

    /// <summary>设置页「刷新设备列表」：重新枚举输出设备（插拔音频设备后点击）。</summary>
    private void RefreshOutputDevices_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshOutputDevices();
    }

    /// <summary>双击专辑搜索结果：加载专辑歌曲并跳到歌曲列表页。</summary>
    private async void AlbumList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AlbumList.SelectedItem is Album album)
        {
            await _viewModel.LoadAlbumSongsAsync(album);
            ShowTrackList(SearchIndex);
        }
    }

    /// <summary>双击歌手搜索结果：加载歌手热门歌曲并跳到歌曲列表页。</summary>
    private async void ArtistList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ArtistList.SelectedItem is Artist artist)
        {
            await _viewModel.LoadArtistTopSongsAsync(artist);
            ShowTrackList(SearchIndex);
        }
    }

    /// <summary>空格 = 播放/暂停。组合键、输入框/下拉/按钮焦点下不劫持。</summary>
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        // 焦点在输入框/下拉/按钮上时不劫持空格，避免打字误触或与按钮激活冲突。
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or Button)
        {
            return;
        }

        await _viewModel.TogglePlayPauseAsync();
        e.Handled = true;
    }

    /// <summary>当前是否正在拖拽 Thumb。拖拽期间收到定时器回写不 Seek。</summary>
    private bool _isDragging;

    /// <summary>
    /// 拖拽 Thumb 开始 — 暂停定时器更新，避免滑块在拖拽中跳动。
    /// </summary>
    private void ProgressSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _isDragging = true;
        _viewModel.BeginSeek();
    }

    /// <summary>
    /// 拖拽 Thumb 松手 — 用 Slider.Value（拖拽后的位置）跳转。
    /// </summary>
    private void ProgressSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (sender is Slider slider)
        {
            _viewModel.EndSeek(slider.Value);
        }

        _isDragging = false;
    }

    /// <summary>
    /// Slider 的 Value 变化分流：
    /// 1. 用户点击轨道：WPF 的 IsMoveToPointEnabled 会把 Thumb 移到点击处并回写 Value
    ///    （OneWay 绑定时代码收不到，TwoWay 绑定可收到）→ 直接 Seek。
    /// 2. 用户拖拽 Thumb：Value 连续变化，只取松手那次的 DragCompleted 结果 → 跳过。
    /// 3. 播放定时器回写 PositionRatio：此时 Slider 属于被动跟随 → 跳过。
    /// 通过 _isDragging 和 _viewModel.IsPositionUpdateFromPlayback 区分以上场景。
    /// </summary>
    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 拖拽中：由 DragCompleted 统一 Seek，避免拖拽过程中反复跳转。
        // 定时器回写：不应触发 Seek。
        if (_isDragging || _viewModel.IsPositionUpdateFromPlayback)
        {
            return;
        }

        // 尚未开始播放（没有时长信息）时忽略点击。
        if (sender is Slider slider)
        {
            _viewModel.EndSeek(slider.Value);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosing(e);
    }
}
