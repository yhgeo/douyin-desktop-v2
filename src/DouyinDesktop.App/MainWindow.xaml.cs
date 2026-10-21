using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DouyinDesktop.Core.Api;
using DouyinDesktop.Core.Models;
using DouyinDesktop.Player;

namespace DouyinDesktop.App;

public partial class MainWindow : Window
{
    private readonly DouyinClient _client = DouyinClient.Create();
    private readonly VideoStreamProxy _proxy = VideoStreamProxy.Start();
    private readonly KeyMap _keyMap = KeyMap.Load();
    private readonly AppSettings _settings = AppSettings.Load();
    /// <summary>
    /// 一个信息流的状态：列表、已展示过的 id、当前看到第几条。
    /// 推荐 / 关注 / 作者主页各存一份，切换时互不影响。
    /// </summary>
    private sealed class FeedContext
    {
        public List<Aweme> Items { get; } = new();
        public HashSet<string> Presented { get; } = new();
        public int Index { get; set; } = -1;
    }

    private readonly FeedContext _recommendFeed = new();
    private readonly FeedContext _followFeed = new();
    private readonly FeedContext _authorFeed = new();
    private FeedContext _feed = null!;

    // 用属性包一层，原有代码里的 _items / _index / _presented 全部不用改
    private List<Aweme> _items => _feed.Items;
    private int _index { get => _feed.Index; set => _feed.Index = value; }
    private HashSet<string> _presented => _feed.Presented;
    private bool _useScrollFeed;
    /// <summary>当前是不是「关注」页（只刷关注的人）。</summary>
    private bool _followMode;
    private int _refreshIndex = 2;

    // 图集：图片走单独的 HttpClient，缓存最近看过的几十张
    private readonly HttpClient _imageHttp = CreateImageHttp();
    private readonly Dictionary<string, byte[]> _imageCache = new();
    private bool _imageMode;
    private Aweme? _imageItem;
    private int _imageIndex;

    private MpvController? _mpv;
    private bool _mpvStarting;
    private bool _feedLoading;
    private bool _fullscreen;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private Rect _boundsBeforeFullscreen;
    /// <summary>自己维护的最大化状态（不用 WindowState.Maximized，见 ToggleMaximize）。</summary>
    private bool _maximized;
    private Rect _boundsBeforeMaximize;
    private bool _adjustingState;
    /// <summary>当前列表是不是某个作者的作品（此时不再自动补推荐流，否则会混在一起）。</summary>
    private bool _authorMode;
    private bool _muted;
    private int _volume = 100;
    private double _aspect = 9.0 / 16.0;

    private readonly Stopwatch _switchTimer = new();
    private readonly List<string> _statusLines = new();
    private CancellationTokenSource? _prefetchCts;
    private int _consecutiveFailures;
    private bool _lastFromCache;
    private long _apiBitrate;

    public MainWindow()
    {
        InitializeComponent();
        _feed = _recommendFeed;
        CommentList.ItemsSource = _commentVms;
        _client.Log += msg => Dispatcher.BeginInvoke(() => PushStatus(msg));
        Surface.HandleCreated += OnSurfaceReady;
    }

    /// <summary>
    /// 彻底关掉主窗口的输入法。
    ///
    /// 只设 InputMethod.IsInputMethodEnabled 不够——那只管 WPF 的输入焦点，
    /// 系统 IME 仍然会在窗口激活时抢单键（按 F 弹出候选框，全屏就失效了）。
    /// ImmAssociateContext(hwnd, 0) 是直接断掉窗口和输入法上下文的关联。
    /// 主窗口没有任何文本输入，禁用没有任何副作用。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        ImmAssociateContext(hwnd, IntPtr.Zero);
    }

    /// <summary>
    /// 点画面暂停 / 继续。
    ///
    /// 视频画布那个 Win32 子窗口带 WS_EX_TRANSPARENT（收不到真实鼠标消息），
    /// 点击会落到 WPF 层，由 VideoHost 的 MouseLeftButtonUp 冒泡到这里。
    /// 图集模式下不处理——图集没有画面可暂停。
    /// </summary>
    private async void OnVideoAreaClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_imageMode || _mpv is null) return;

        await _mpv.TogglePauseAsync();

        // mpv 的 pause 是异步事件回来的，等一拍再读才准
        await Task.Delay(120);
        AppLog.Write($"点画面：暂停={_mpv.IsPaused}");
    }

    [System.Runtime.InteropServices.DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hImc);

    /// <summary>
    /// 每次窗口重新激活都断一次输入法。
    /// 点过视频区（HwndHost 是独立 Win32 窗口）之后，输入法上下文会重新挂到主窗口上，
    /// 结果按 G 弹出候选框、快捷键全部失效。
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        ImmAssociateContext(new System.Windows.Interop.WindowInteropHelper(this).Handle, IntPtr.Zero);
    }

    // ================= 启动 =================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureOnScreen();
        LayoutVideo();
        SetStatus("正在获取推荐…");

        InjectDemoImageIfRequested();
        RefreshShortcuts();

        // 调试用：--author=<sec_uid> 直接打开某个作者的作品列表
        string? authorArg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--author=", StringComparison.Ordinal));
        if (authorArg is not null)
        {
            _ = LoadAuthorPostsAsync(authorArg["--author=".Length..], "指定作者");
            return;
        }

        // 播放器和推荐流并行准备：谁先好都不等谁
        _ = LoadFeedAsync();
        await Task.CompletedTask;
    }

    /// <summary>
    /// 调试入口：--demo-image 会在列表最前面插一条本地图集。
    /// 推荐流（module/feed）实测只返回视频，扫 48 条 images 字段一条都没有，
    /// 所以图集 UI 只能靠注入数据验证。正常启动不会走这里。
    /// </summary>
    private void InjectDemoImageIfRequested()
    {
        if (!Environment.GetCommandLineArgs().Contains("--demo-image")) return;

        string? root = FindProjectRoot();
        if (root is null) { SetStatus("找不到项目根目录，无法加载演示图集"); return; }

        string dir = Path.Combine(root, "tmp", "demo");
        string Url(int i) => new Uri(Path.Combine(dir, $"img{i}.png")).AbsoluteUri;

        if (!File.Exists(Path.Combine(dir, "img1.png")))
        {
            SetStatus("演示图片不存在：" + dir);
            return;
        }

        _items.Insert(0, new Aweme
        {
            Id = "demo-image",
            Desc = "【图集测试】本地注入的假数据，用于验证图集显示与左右翻页",
            Author = new Author { Nickname = "图集测试" },
            Statistics = new Statistics { DiggCount = 1234, CommentCount = 56, ShareCount = 7 },
            Images = new List<ImageInfo>
            {
                new() { Width = 900, Height = 1200, UrlList = new() { Url(1) } },
                new() { Width = 900, Height = 1200, UrlList = new() { Url(2) } },
                new() { Width = 900, Height = 1200, UrlList = new() { Url(3) } },
            },
        });

        PushStatus("已注入演示图集");
    }

    /// <summary>窗口位置存到屏幕外时（换显示器/分辨率变化）拉回中间。</summary>
    private void EnsureOnScreen()
    {
        var wa = SystemParameters.WorkArea;
        bool offscreen = Left + Width < wa.Left + 120
                         || Left > wa.Right - 120
                         || Top + Height < wa.Top + 60
                         || Top > wa.Bottom - 60;

        if (!offscreen) return;

        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
    }

    private static string? FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PLAN.md"))
                && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
        }
        return null;
    }

    private async void OnSurfaceReady(object? sender, IntPtr hwnd)
    {
        // 全屏切换等操作可能让 WPF 重建宿主窗口，句柄一换 mpv 就画到旧窗口上了，
        // 所以这里统一处理：旧实例销毁，新实例挂到新句柄。
        if (_mpv is not null)
        {
            var old = _mpv;
            _mpv = null;
            try { await old.DisposeAsync(); } catch { /* 已经在退出 */ }
        }
        await StartMpvAsync(hwnd);
    }

    private async Task StartMpvAsync(IntPtr hwnd)
    {
        if (_mpvStarting) return;
        _mpvStarting = true;
        try
        {
            string mpvPath = MpvLocator.Find();
            var mpv = await MpvController.StartAsync(new MpvOptions
            {
                MpvPath = mpvPath,
                ParentWindow = hwnd,
                Volume = _volume,
                Muted = _muted,
                LogPath = Path.Combine(AppLog.LogDirectory, $"mpv-{DateTime.Now:yyyyMMdd}.log"),
            });

            mpv.FileLoaded += OnFileLoaded;
            mpv.PlaybackEnded += OnPlaybackEnded;
            mpv.FileFailed += OnFileFailed;
            mpv.StatsChanged += s => Dispatcher.BeginInvoke(() => UpdateTechStats(s));
            mpv.PausedChanged += p => Dispatcher.BeginInvoke(() => SetStatus(p ? "已暂停" : "播放中"));
            mpv.TimePosChanged += OnTimePos;
            mpv.DurationChanged += OnDuration;
            mpv.BufferingChanged += b => Dispatcher.BeginInvoke(() =>
            {
                if (b) SetStatus("缓冲中…");
            });
            mpv.Crashed += msg => Dispatcher.BeginInvoke(() => SetStatus("播放器异常：" + msg));
            mpv.Log += msg => Dispatcher.BeginInvoke(() => PushStatus(msg));

            _mpv = mpv;
            SetStatus("播放器就绪");

            if (_index >= 0) await PlayIndexAsync(_index, force: true);
        }
        catch (Exception ex)
        {
            SetStatus("播放器启动失败：" + ex.Message);
        }
        finally
        {
            _mpvStarting = false;
        }
    }

    // ================= 推荐流 =================

    private async Task LoadFeedAsync(bool reset = false)
    {
        if (_feedLoading) return;
        _feedLoading = true;
        try
        {
            if (reset)
            {
                _items.Clear();
                _presented.Clear();
                _index = -1;
            }

            SetStatus("正在获取推荐…");
            IReadOnlyList<Aweme> got;
            long elapsed = 0;

            if (_followMode)
            {
                // 关注流走 follow/feed，结构是 {cursor, data:[...]}，
                // 而且**必须登录**（游客态 data 是空的）
                if (_client.Session?.IsLoggedIn != true)
                {
                    _followMode = false;
                    SetStatus("关注页需要先登录：设置 → 账号 → 扫码登录");
                    return;
                }

                var r = await _client.GetFeedScrollAsync(10, _refreshIndex++, "follow/feed");
                if (!r.Success)
                {
                    SetStatus("关注流加载失败：" + r.Message);
                    return;
                }

                got = r.Items;
                elapsed = r.ElapsedMs;
            }
            else if (_settings.ImageOnly)
            {
                got = await FetchImagesOnlyAsync();
                if (got.Count == 0)
                {
                    SetStatus("拉了几次都没找到图集，稍后再试（图集只占约 5%）");
                    return;
                }
            }
            else
            {
                // 两个推荐接口交替用：module/feed 和 tab/feed 的内容池不同，
                // 混着拉能增加内容多样性（图集比例也会略有提升）
                var result = _useScrollFeed
                    ? await _client.GetFeedScrollAsync(10, _refreshIndex++)
                    : await _client.GetFeedAsync(15, _presented);
                _useScrollFeed = !_useScrollFeed;

                if (!result.Success)
                {
                    SetStatus("获取失败：" + result.Message);
                    return;
                }

                got = result.Items;
                elapsed = result.ElapsedMs;
            }

            foreach (var a in got)
            {
                if (_presented.Add(a.Id)) _items.Add(a);
            }

            PushStatus($"推荐流 +{got.Count} 条 / {elapsed}ms");

            if (_index < 0 && _items.Count > 0)
                await PlayIndexAsync(0);
            else
                UpdateIndexLabel();
        }
        catch (Exception ex)
        {
            SetStatus("获取异常：" + ex.Message);
        }
        finally
        {
            _feedLoading = false;
        }
    }

    /// <summary>
    /// 只看图集：循环拉推荐流，把视频丢掉，只留图集。
    ///
    /// 代价要说清楚：图集在推荐流里约占 5%，凑 8 条要拉 160 条数据（约 8 次请求）。
    /// 所以这里硬性限制最多 6 次，拉不到就返回空，不会无限请求。
    /// </summary>
    private async Task<IReadOnlyList<Aweme>> FetchImagesOnlyAsync()
    {
        // 实测游客态几乎刷不到图集（IP 属地也不返回），登录后才正常
        if (_client.Session?.IsLoggedIn != true)
        {
            SetStatus("只看图集需要先登录：游客态下抖音基本不推图文");
            return Array.Empty<Aweme>();
        }

        var images = new List<Aweme>();
        var seen = new HashSet<string>();

        for (int i = 0; i < 6 && images.Count < 8; i++)
        {
            SetStatus($"只看图集：第 {i + 1} 次筛选，已找到 {images.Count} 条…");

            var r = _useScrollFeed
                ? await _client.GetFeedScrollAsync(10, _refreshIndex++)
                : await _client.GetFeedAsync(20, _presented);
            _useScrollFeed = !_useScrollFeed;

            if (!r.Success) break;

            foreach (var a in r.Items)
            {
                if (!a.IsImagePost) continue;
                if (_presented.Contains(a.Id) || !seen.Add(a.Id)) continue;
                images.Add(a);
            }

            if (images.Count >= 8) break;
            await Task.Delay(350);
        }

        PushStatus($"只看图集：找到 {images.Count} 条");
        return images;
    }

    private void UpdateMetaLine(Aweme item, string ip)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(ip)) parts.Add(ip);
        if (item.CreateTime > 0) parts.Add(item.CreatedLocal.ToString("yyyy-MM-dd HH:mm"));

        MetaLine.Text = string.Join(" · ", parts);
        MetaLine.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 切换信息流。目标流如果之前看过，直接恢复上次的位置和列表，
    /// 不重新拉数据——切来切去不该把推荐流刷没。
    /// </summary>
    private async Task SwitchFeedAsync(FeedContext target, string label)
    {
        _feed = target;
        UpdateModeLabel();

        if (target.Items.Count > 0)
        {
            int idx = Math.Clamp(target.Index, 0, target.Items.Count - 1);
            await PlayIndexAsync(idx, force: true);
            UpdateIndexLabel();
            SetStatus($"{label}：{target.Items.Count} 条（已恢复上次的位置）");
        }
        else
        {
            SetStatus($"正在加载{label}…");
            await LoadFeedAsync(reset: false);
        }
    }

    /// <summary>
    /// 缓冲区快见底时补货。
    /// 留 6 条的余量而不是 3 条：拉一批要 2 秒多，余量太小的话
    /// 用户切到末尾时会干等，表现为"播到第 11 个又要重新加载"。
    /// </summary>
    private void EnsureBuffer()
    {
        // 作者主页模式下不补推荐流——混进来就不是"这个人的作品"了
        if (_feedLoading || _authorMode) return;
        if (_items.Count - _index <= 6)
            _ = LoadFeedAsync();
    }

    /// <summary>
    /// 加载指定 sec_uid 的全部作品。
    /// 这是图集的唯一入口——推荐流几乎不推图文（实测 48 条里 0 条）。
    /// </summary>
    private async Task LoadAuthorPostsAsync(string secUid, string nickname)
    {
        if (string.IsNullOrEmpty(secUid))
        {
            SetStatus("拿不到作者信息");
            return;
        }

        SetStatus($"正在加载 @{nickname} 的作品…");
        var result = await _client.GetUserPostsAsync(secUid);
        if (!result.Success)
        {
            SetStatus("加载失败：" + result.Message);
            return;
        }

        _authorMode = true;
        _followMode = false;
        _feed = _authorFeed;
        _authorFeed.Items.Clear();
        _authorFeed.Presented.Clear();
        _authorFeed.Index = -1;
        _authorFeed.Items.AddRange(result.Items);

        int imageCount = result.Items.Count(x => x.IsImagePost);
        AppLog.Write($"作者作品：{result.Items.Count} 条，其中图集 {imageCount} 条");
        await PlayIndexAsync(0);
        SetStatus($"@{nickname} 的 {result.Items.Count} 条作品" +
                  (imageCount > 0 ? $"（含 {imageCount} 条图集）" : "") +
                  "，按 R 回推荐");
    }

    private Task LoadCurrentAuthorAsync()
    {
        if (_index < 0 || _index >= _items.Count) return Task.CompletedTask;
        var author = _items[_index].Author;
        return LoadAuthorPostsAsync(author?.SecUid ?? "", author?.Nickname ?? "作者");
    }

    // ================= 播放 =================

    private async Task PlayIndexAsync(int index, bool force = false)
    {
        if (index < 0 || index >= _items.Count) return;

        var item = _items[index];

        // 图集作品没有视频流，走图片展示
        if (item.IsImagePost)
        {
            _index = index;
            ShowInfo(item);
            await ShowImagePostAsync(item);
            UpdateIndexLabel();
            PrefetchAround(index);
            EnsureBuffer();
            return;
        }

        if (!item.IsPlayable && !force)
        {
            // 既不是视频也不是图集：跳过，避免卡住
            PushStatus($"跳过不可播条目 {item.Id}");
            if (index < _items.Count - 1) { await PlayIndexAsync(index + 1); return; }
            return;
        }

        ExitImageMode();
        _index = index;
        ShowInfo(item);

        if (_mpv is null)
        {
            SetStatus("等待播放器…");
            return;
        }

        if (string.IsNullOrEmpty(item.Video?.PlayAddr?.Uri))
        {
            SetStatus("这条没有可播地址");
            return;
        }

        _switchTimer.Restart();
        // 走本地代理：开头那段如果已经预取过，mpv 立刻就有数据可解
        string url = _proxy.UrlFor(item);

        try
        {
            await _mpv.LoadAsync(url);
        }
        catch (Exception ex)
        {
            SetStatus("加载失败：" + ex.Message);
        }

        _lastFromCache = _proxy.HasPrefetch(item.Id);
        PrefetchAround(index);
        UpdateIndexLabel();
        EnsureBuffer();
    }

    // ================= 评论 =================

    private long _commentCursor;
    private bool _commentsLoading;
    private bool _commentsHasMore;
    private string _commentAwemeId = "";
    private readonly System.Collections.ObjectModel.ObservableCollection<CommentVm> _commentVms = new();

    private string CurrentId
        => _index >= 0 && _index < _items.Count ? _items[_index].Id : "";

    private enum SideTab { Detail, Comments, Profile }

    private SideTab _sideTab = SideTab.Detail;

    private readonly Dictionary<string, IReadOnlyList<Aweme>> _profileCache = new();
    private string _profileSecUid = "";
    private bool _profileSelecting;

    private void OnTabDetailClick(object sender, RoutedEventArgs e) => SwitchTab(SideTab.Detail);
    private void OnTabCommentsClick(object sender, RoutedEventArgs e) => SwitchTab(SideTab.Comments);
    private void OnTabProfileClick(object sender, RoutedEventArgs e) => SwitchTab(SideTab.Profile);

    private void SwitchTab(SideTab tab)
    {
        _sideTab = tab;

        TabDetail.Tag = tab == SideTab.Detail ? "active" : null;
        TabComments.Tag = tab == SideTab.Comments ? "active" : null;
        TabProfile.Tag = tab == SideTab.Profile ? "active" : null;

        DetailPane.Visibility = tab == SideTab.Detail ? Visibility.Visible : Visibility.Collapsed;
        CommentsPane.Visibility = tab == SideTab.Comments ? Visibility.Visible : Visibility.Collapsed;
        ProfilePane.Visibility = tab == SideTab.Profile ? Visibility.Visible : Visibility.Collapsed;

        if (tab == SideTab.Comments) _ = LoadCommentsAsync(reset: _commentAwemeId != CurrentId);
        if (tab == SideTab.Profile) _ = LoadProfileAsync();
    }

    private async Task LoadCommentsAsync(bool reset)
    {
        string id = CurrentId;
        if (string.IsNullOrEmpty(id) || _commentsLoading) return;

        if (reset || id != _commentAwemeId)
        {
            _commentAwemeId = id;
            _commentCursor = 0;
            _commentsHasMore = true;
            _commentVms.Clear();
            CommentEmpty.Visibility = Visibility.Collapsed;
        }
        if (!_commentsHasMore) return;

        _commentsLoading = true;
        try
        {
            var page = await _client.GetCommentsAsync(id, _commentCursor);

            // 加载期间用户可能已经切走了，别把上一条的评论塞进来
            if (id != CurrentId) return;

            foreach (var c in page.Comments) _commentVms.Add(new CommentVm(c));
            _commentCursor = page.Cursor;
            _commentsHasMore = page.HasMore;

            TabComments.Content = page.Total > 0 ? $"评论 {Count(page.Total)}" : "评论";

            if (_commentVms.Count == 0)
            {
                CommentEmpty.Text = page.Total == 0 ? "暂无评论" : "评论加载失败（可能被风控）";
                CommentEmpty.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            SetStatus("评论加载失败：" + ex.Message);
        }
        finally
        {
            _commentsLoading = false;
        }
    }

    /// <summary>滚到接近底部就续一页。</summary>
    private void OnCommentScroll(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight <= 0) return;
        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 240) return;
        if (_commentsHasMore && !_commentsLoading) _ = LoadCommentsAsync(reset: false);
    }

    // ================= 作者主页 Tab =================

    /// <summary>
    /// 主页 Tab：作者的视频封面墙。只拉作品列表（含封面图 URL），不碰视频流。
    /// </summary>
    private async Task LoadProfileAsync()
    {
        if (_index < 0 || _index >= _items.Count) return;

        var author = _items[_index].Author;
        if (author is null || string.IsNullOrEmpty(author.SecUid))
        {
            ProfileName.Text = "拿不到作者信息";
            ProfileList.ItemsSource = null;
            return;
        }

        ProfileName.Text = "@" + author.Nickname;

        // 同一个作者不重复拉
        if (_profileSecUid == author.SecUid && ProfileList.ItemsSource is not null) return;

        if (_profileCache.TryGetValue(author.SecUid, out var cached))
        {
            ShowProfile(author, cached);
            return;
        }

        ProfileCount.Text = "加载中…";
        var result = await _client.GetUserPostsAsync(author.SecUid);
        if (!result.Success)
        {
            ProfileCount.Text = "";
            SetStatus("主页加载失败：" + result.Message);
            return;
        }

        if (_profileCache.Count > 30) _profileCache.Clear();
        _profileCache[author.SecUid] = result.Items;
        ShowProfile(author, result.Items);
    }

    private void ShowProfile(Author author, IReadOnlyList<Aweme> posts)
    {
        _profileSecUid = author.SecUid ?? "";
        ProfileList.ItemsSource = posts;
        ProfileCount.Text = $"{posts.Count} 条作品 · 点封面直接播";

        // 把当前正在播的那条标出来
        SyncProfileSelection();

        if (author.AvatarThumb?.First is { Length: > 0 } avatar)
        {
            try { ProfileAvatar.Source = new BitmapImage(new Uri(avatar)); }
            catch (UriFormatException) { ProfileAvatar.Source = null; }
        }
        else
        {
            ProfileAvatar.Source = null;
        }
    }

    /// <summary>当前播放的这条在封面墙上高亮（程序设置选中，不触发播放）。</summary>
    private void SyncProfileSelection()
    {
        if (ProfileList.ItemsSource is not IReadOnlyList<Aweme> posts) return;

        _profileSelecting = true;
        ProfileList.SelectedItem = posts.FirstOrDefault(x => x.Id == CurrentId);
        _profileSelecting = false;
    }

    private async void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_profileSelecting) return;                     // 程序设的选中，不是用户点的
        if (ProfileList.SelectedItem is not Aweme item) return;
        if (ProfileList.ItemsSource is not IReadOnlyList<Aweme> posts) return;

        int idx = _items.IndexOf(item);

        // 主页列表和当前流不是一回事：先把这批作品灌进「作者主页」流
        if (idx < 0)
        {
            _authorMode = true;
            _followMode = false;
            _feed = _authorFeed;
            _authorFeed.Items.Clear();
            _authorFeed.Presented.Clear();
            _authorFeed.Items.AddRange(posts);
            UpdateModeLabel();

            idx = _authorFeed.Items.IndexOf(item);
            if (idx < 0) return;
        }

        AppLog.Write($"主页点封面：{item.Id} -> 第 {idx + 1} 条");
        await PlayIndexAsync(idx, force: true);
    }

    /// <summary>右下角显示当前在哪个流。</summary>
    private void UpdateModeLabel()
    {
        string text = _authorMode ? "作者主页" : _followMode ? "关注" : "推荐";
        ModeLabel.Text = text;

        // 非推荐流换个底色，一眼能看出来
        ModeBadge.Background = new System.Windows.Media.SolidColorBrush(
            _authorMode || _followMode
                ? System.Windows.Media.Color.FromRgb(0x3A, 0x1D, 0x26)
                : System.Windows.Media.Color.FromRgb(0x1F, 0x1F, 0x28));
    }

    private void OnToggleReplies(object sender, RoutedEventArgs e)
    {
        AppLog.Write($"点击展开回复 sender={sender?.GetType().Name}");

        if (sender is not FrameworkElement { Tag: CommentVm vm })
        {
            AppLog.Write("  Tag 不是 CommentVm，绑定有问题");
            return;
        }

        if (vm.IsExpanded)
        {
            vm.Collapse();
            return;
        }

        _ = ExpandRepliesAsync(vm);
    }

    private async Task ExpandRepliesAsync(CommentVm vm)
    {
        string awemeId = CurrentId;
        if (string.IsNullOrEmpty(awemeId)) return;

        vm.SetLoading(true);
        try
        {
            var page = await _client.GetCommentRepliesAsync(awemeId, vm.Model.Id);
            if (page.Comments.Count == 0)
            {
                vm.SetLoading(false);
                SetStatus("没有更多回复");
                return;
            }
            vm.Expand(page.Comments);
        }
        catch (Exception ex)
        {
            vm.SetLoading(false);
            SetStatus("加载回复失败：" + ex.Message);
        }
    }

    // ================= 图集 =================

    private static HttpClient CreateImageHttp()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DeviceProfile.DefaultUserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.douyin.com/");
        return http;
    }

    private async Task ShowImagePostAsync(Aweme item)
    {
        _imageMode = true;
        _imageItem = item;
        _imageIndex = 0;

        AppLog.Write($"图集作品 {item.Id}：{item.Images?.Count ?? 0} 张");

        // HwndHost 永远盖在 WPF 内容之上，必须先把它藏起来，图片才看得见
        Surface.Visibility = Visibility.Collapsed;
        ImageLayer.Visibility = Visibility.Visible;

        if (_mpv is not null)
        {
            string audio = item.Music?.AudioUrl ?? "";
            if (audio.Length > 0)
            {
                // 图集没有视频流，但背景音乐照放
                try { await _mpv.LoadAudioAsync(audio); }
                catch (Exception ex) { PushStatus("音乐播放失败：" + ex.Message); }
            }
            else
            {
                try { await _mpv.StopAsync(); } catch { /* 已经停了 */ }
            }
        }

        await LoadImageAsync();
    }

    private async Task LoadImageAsync()
    {
        if (_imageItem?.Images is not { Count: > 0 } images) return;
        if (_imageIndex < 0 || _imageIndex >= images.Count) return;

        var info = images[_imageIndex];
        ImageIndexText.Text = $"{_imageIndex + 1} / {images.Count}";

        try
        {
            byte[] bytes = await GetImageBytesAsync(info);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            ImageView.Source = bmp;
        }
        catch (Exception ex)
        {
            SetStatus("图片加载失败：" + ex.Message);
        }
    }

    private async Task<byte[]> GetImageBytesAsync(ImageInfo info)
    {
        var urls = info.UrlList ?? new List<string>();
        foreach (string url in urls)
        {
            if (string.IsNullOrEmpty(url)) continue;
            if (_imageCache.TryGetValue(url, out var cached)) return cached;

            try
            {
                // 调试用的本地图集（--demo-image）走文件读取，其余走 HTTP
                if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    string local = new Uri(url).LocalPath;
                    if (!File.Exists(local)) continue;
                    byte[] localBytes = await File.ReadAllBytesAsync(local);
                    _imageCache[url] = localBytes;
                    return localBytes;
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _imageHttp.SendAsync(req);
                if (!resp.IsSuccessStatusCode) continue;

                byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0) continue;

                if (_imageCache.Count > 60) _imageCache.Clear();
                _imageCache[url] = bytes;
                return bytes;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // 这个 CDN 不行就试下一个地址
            }
        }
        throw new InvalidOperationException("所有图片地址都不可用");
    }

    private async Task StepImageAsync(int delta)
    {
        if (!_imageMode || _imageItem?.Images is not { Count: > 0 } images) return;

        int next = Math.Clamp(_imageIndex + delta, 0, images.Count - 1);
        if (next == _imageIndex) return;
        _imageIndex = next;
        await LoadImageAsync();
    }

    private void ExitImageMode()
    {
        if (!_imageMode) return;
        _imageMode = false;
        _imageItem = null;
        ImageView.Source = null;
        ImageLayer.Visibility = Visibility.Collapsed;
        Surface.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 以当前视频为中心，预取前后各两条的开头一段。
    ///
    /// 顺序就是优先级：+1 最要紧（马上要切），然后 -1（回退），再 +2/-2。
    /// 每次切换取消上一轮——用户快速连按时，旧任务继续下只会挤占最新那条的带宽。
    /// 每条只取 1MB，不是整条下载（实测有的视频 207MB，全下既慢又浪费）。
    /// </summary>
    private void PrefetchAround(int index)
    {
        _prefetchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _prefetchCts = cts;

        _ = Task.Run(async () =>
        {
            // 顺序即优先级：用户主要往下刷，所以 +1 > +2 > -1。
            // 带宽不够时，先保证马上要切的那条完整，回退那条放到最后。
            foreach (int offset in new[] { 1, 2, -1 })
            {
                if (cts.IsCancellationRequested) return;

                int i = index + offset;
                if (i < 0 || i >= _items.Count) continue;

                var next = _items[i];
                if (!next.IsPlayable) continue;

                try { await _proxy.PrefetchAsync(next, cts.Token); }
                catch (OperationCanceledException) { return; }
                catch { /* 预取失败就等播放时透传 */ }
            }
        });
    }

    private async Task NextAsync()
    {
        if (_items.Count == 0) return;

        if (_index + 1 < _items.Count)
        {
            await PlayIndexAsync(_index + 1);
            return;
        }

        // 已经到缓冲区末尾：等下一批
        SetStatus("正在加载更多…");
        await LoadFeedAsync();
        if (_index + 1 < _items.Count) await PlayIndexAsync(_index + 1);
        else SetStatus("没有更多了");
    }

    private async Task PrevAsync()
    {
        if (_index > 0) await PlayIndexAsync(_index - 1);
        else SetStatus("已经是第一条");
    }

    private void OnFileLoaded()
    {
        long ms = _switchTimer.IsRunning ? _switchTimer.ElapsedMilliseconds : 0;
        _switchTimer.Stop();
        _consecutiveFailures = 0;
        string source = _lastFromCache ? "已预取" : "网络";
        Dispatcher.BeginInvoke(() =>
            SetStatus(ms > 0 ? $"已加载（{source}），耗时 {ms}ms" : "已加载"));
    }

    private void OnFileFailed(string reason)
    {
        // 图集放的是背景音乐，音乐挂了不该把整个作品跳过
        if (_imageMode)
        {
            Dispatcher.BeginInvoke(() => SetStatus("背景音乐加载失败"));
            return;
        }

        Dispatcher.BeginInvoke(async () =>
        {
            _consecutiveFailures++;
            string id = _index >= 0 && _index < _items.Count ? _items[_index].Id : "?";
            AppLog.Write($"加载失败 #{_consecutiveFailures} {id}: {reason}");
            SetStatus($"加载失败：{reason}");

            // 一条失败就跳过没问题；连续失败说明网络或凭据出了状况，继续跳只会刷完整个列表
            if (_consecutiveFailures >= 3)
            {
                SetStatus("连续 3 条加载失败。按 R 换一批，或检查网络。");
                return;
            }
            try { await NextAsync(); } catch (Exception ex) { SetStatus("切换失败：" + ex.Message); }
        });
    }

    private void OnPlaybackEnded()
    {
        Dispatcher.BeginInvoke(async () => await NextAsync());
    }

    private void OnTimePos(double pos)
    {
        Dispatcher.BeginInvoke(() =>
        {
            double dur = _mpv?.Duration ?? 0;
            Progress.Value = dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0;
            TimeText.Text = $"{Fmt(pos)} / {Fmt(dur)}";
        });
    }

    private void OnDuration(double dur)
    {
        Dispatcher.BeginInvoke(() => TimeText.Text = $"{Fmt(_mpv?.Position ?? 0)} / {Fmt(dur)}");
    }

    // ================= 界面 =================

    private void ShowInfo(Aweme item)
    {
        AuthorText.Text = "@" + item.AuthorName;

        UpdateMetaLine(item, item.IpLabel);
        DescText.Text = string.IsNullOrWhiteSpace(item.Desc) ? "（无文案）" : item.Desc;

        // 固定高度里放不下就缩字号（14 → 13 → 12），配合滚动条
        DescText.FontSize = item.Desc.Length switch
        {
            > 220 => 12,
            > 120 => 13,
            _ => 14,
        };
        DescText.LineHeight = DescText.FontSize + 9;
        DescScroll.ScrollToTop();

        var st = item.Statistics;
        LikeText.Text = $"♥ {Count(st?.DiggCount ?? 0)}";
        CommentText.Text = $"💬 {Count(st?.CommentCount ?? 0)}";
        ShareText.Text = $"↗ {Count(st?.ShareCount ?? 0)}";

        MusicText.Text = item.Music is { Title.Length: > 0 }
            ? $"♪ {item.Music.Title} — {item.Music.Author}"
            : item.IsImagePost ? "♪ 无背景音乐" : "";

        ResetTechStats();

        if (item.IsImagePost && item.Images is { Count: > 0 } imgs0)
        {
            // 图集没有视频流：显示图片张数和尺寸，而不是 mpv 的分辨率/帧率
            VideoStatsPanel.Visibility = Visibility.Collapsed;
            ImageStatsPanel.Visibility = Visibility.Visible;
            ImageCountText.Text = $"{imgs0.Count} 张";
            ImageSizeText.Text = imgs0[0].Width > 0
                ? $"{imgs0[0].Width}×{imgs0[0].Height}"
                : "—";
        }
        else
        {
            VideoStatsPanel.Visibility = Visibility.Visible;
            ImageStatsPanel.Visibility = Visibility.Collapsed;
            _apiBitrate = item.Video?.MaxBitRate ?? 0;
            TechBitrate.Text = FormatBitrate(_apiBitrate);
        }

        // 评论数先用作品自带的统计值，点进评论页后再用接口返回的 total 校正
        long commentTotal = item.Statistics?.CommentCount ?? 0;
        TabComments.Content = commentTotal > 0 ? $"评论 {Count(commentTotal)}" : "评论";
        _commentAwemeId = "";

        // 换视频后回到顶部，否则长文案还停在上一条的滚动位置
        DetailPane.ScrollToTop();

        // 图集用第一张图的宽高比，视频用视频的
        _aspect = item.IsImagePost && item.Images is { Count: > 0 } imgs
            ? (imgs[0].Width > 0 && imgs[0].Height > 0
                ? (double)imgs[0].Width / imgs[0].Height
                : 3.0 / 4.0)
            : item.Video?.Aspect ?? 9.0 / 16.0;

        LayoutVideo();
        UpdateIndexLabel();

        // 正在看评论/主页的话，切作品就跟着换
        if (_sideTab == SideTab.Comments) _ = LoadCommentsAsync(reset: true);
        if (_sideTab == SideTab.Profile)
        {
            _ = LoadProfileAsync();
            SyncProfileSelection();
        }
    }

    private void UpdateTechStats(VideoStats s)
    {
        // 图集没有视频流，mpv 可能还在报上一条视频的参数
        if (_imageMode) return;

        TechRes.Text = s.ResolutionText;
        TechFps.Text = s.FpsText;
        // mpv 对网络流常常不报 video-bitrate，用接口给的档位码率兜底
        TechBitrate.Text = s.BitrateBps > 0 ? s.BitrateText : FormatBitrate(_apiBitrate);
    }

    private void ResetTechStats()
        => TechRes.Text = TechFps.Text = TechBitrate.Text = "—";

    private static string FormatBitrate(long bps) => bps switch
    {
        >= 1_000_000 => $"{bps / 1_000_000.0:0.##} Mbps",
        > 0 => $"{bps / 1000.0:0} kbps",
        _ => "—",
    };

    private void UpdateIndexLabel()
        => IndexLabel.Text = _items.Count == 0 ? "" : $"{_index + 1} / {_items.Count}";

    private void LayoutVideo()
    {
        double w = VideoHost.ActualWidth, h = VideoHost.ActualHeight;
        if (w <= 1 || h <= 1) return;

        double aspect = _aspect > 0.1 ? _aspect : 9.0 / 16.0;
        double targetW, targetH;
        if (w / h > aspect) { targetH = h; targetW = h * aspect; }
        else { targetW = w; targetH = w / aspect; }

        Surface.Width = Math.Max(1, targetW);
        Surface.Height = Math.Max(1, targetH);
    }

    private void OnVideoHostSizeChanged(object sender, SizeChangedEventArgs e) => LayoutVideo();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => LayoutVideo();

    private void SetStatus(string msg)
    {
        StatusText.Text = msg;
    }

    /// <summary>技术日志：界面上只显示最后一条，完整记录写文件。</summary>
    private void PushStatus(string msg)
    {
        _statusLines.Add(msg);
        if (_statusLines.Count > 30) _statusLines.RemoveAt(0);
        AppLog.Write(msg);
    }

    // ================= 输入 =================

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            // Esc 不进快捷键表：它只用来退出全屏
            if (e.Key == Key.Escape)
            {
                if (_fullscreen) { ToggleFullscreen(); e.Handled = true; }
                return;
            }

            // Ctrl+O 打开设置（不进快捷键表，避免和普通按键冲突）
            if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                OnSettingsClick(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            string? action = _keyMap.ActionFor(e.Key);
            if (action is null) return;

            e.Handled = true;

            switch (action)
            {
                case "next":
                    await NextAsync();
                    break;

                case "prev":
                    await PrevAsync();
                    break;

                case "pause":
                    if (_mpv is not null) await _mpv.TogglePauseAsync();
                    break;

                case "seekBack":
                    if (_imageMode) { await StepImageAsync(-1); break; }
                    if (_mpv is not null)
                        await _mpv.SeekAsync(Math.Max(0, _mpv.Position - 5));
                    break;

                case "seekForward":
                    if (_imageMode) { await StepImageAsync(1); break; }
                    if (_mpv is not null)
                        await _mpv.SeekAsync(Math.Min(_mpv.Duration, _mpv.Position + 5));
                    break;

                case "mute":
                    await ToggleMuteAsync();
                    break;

                case "fullscreen":
                    ToggleFullscreen();
                    break;

                case "refresh":
                    _authorMode = false;
                    _followMode = false;
                    _feed = _recommendFeed;
                    UpdateModeLabel();
                    SetStatus("换一批…");
                    await LoadFeedAsync(reset: true);
                    break;

                case "author":
                    // 和主页 Tab 统一：按 U 直接切过去看封面墙
                    SwitchTab(SideTab.Profile);
                    break;

                case "follow":
                    _followMode = !_followMode;
                    _authorMode = false;
                    await SwitchFeedAsync(
                        _followMode ? _followFeed : _recommendFeed,
                        _followMode ? "关注" : "推荐");
                    break;

                case "tab":
                    SwitchTab(_sideTab switch
                    {
                        SideTab.Detail => SideTab.Comments,
                        SideTab.Comments => SideTab.Profile,
                        _ => SideTab.Detail,
                    });
                    break;

                case "login":
                    await HandleLoginAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus("操作失败：" + ex.Message);
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta < 0) _ = NextAsync();
        else if (e.Delta > 0) _ = PrevAsync();
        e.Handled = true;
    }

    private async Task ToggleMuteAsync()
    {
        if (_mpv is null) return;
        _muted = !_muted;
        await _mpv.SetMuteAsync(_muted);
        SetStatus(_muted ? "已静音" : "已取消静音");
    }

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;

        // 只动可见性和窗口位置，不动 WindowStyle —— 改样式会让 WPF 重建窗口句柄，
        // mpv 就画到旧句柄上去了，只能重启播放器（表现为黑屏重新缓冲）
        TitleBar.Visibility = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
        SidePanel.Visibility = _fullscreen ? Visibility.Collapsed : Visibility.Visible;

        if (_fullscreen)
        {
            _stateBeforeFullscreen = WindowState;
            _boundsBeforeFullscreen = new Rect(Left, Top, Width, Height);

            // WindowState=Maximized 只会铺到工作区，任务栏还露着。
            // 无边框窗口直接铺到整块屏幕，才是真的全屏。
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            Topmost = true;
        }
        else
        {
            Topmost = false;
            Left = _boundsBeforeFullscreen.Left;
            Top = _boundsBeforeFullscreen.Top;
            Width = _boundsBeforeFullscreen.Width;
            Height = _boundsBeforeFullscreen.Height;
            WindowState = _stateBeforeFullscreen;
        }

        LayoutVideo();
    }

    // ================= 标题栏按钮 =================

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (_fullscreen) { ToggleFullscreen(); return; }
        ToggleMaximize();
    }

    /// <summary>
    /// 自己实现最大化：不用 WindowState.Maximized。
    ///
    /// 因为 WindowChrome 的 Maximized 是按**屏幕**尺寸算的（2560×1440），
    /// 不是工作区（2560×1400），底部 48px 会盖在任务栏下面，
    /// 而且 MaxHeight 对它无效。直接设 Left/Top/Width/Height 到工作区最省事。
    /// </summary>
    private void ToggleMaximize()
    {
        if (!_maximized)
        {
            _boundsBeforeMaximize = new Rect(Left, Top, Width, Height);
            _maximized = true;

            var wa = SystemParameters.WorkArea;
            Left = wa.Left;
            Top = wa.Top;
            Width = wa.Width;
            Height = wa.Height;
        }
        else
        {
            _maximized = false;
            Left = _boundsBeforeMaximize.Left;
            Top = _boundsBeforeMaximize.Top;
            Width = _boundsBeforeMaximize.Width;
            Height = _boundsBeforeMaximize.Height;
        }

        UpdateMaxIcon();
        LayoutVideo();
    }

    private void UpdateMaxIcon()
    {
        MaxIcon.Data = System.Windows.Media.Geometry.Parse(_maximized
            ? "M2.5,0.5 L9.5,0.5 L9.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5"
            : "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_keyMap, _settings, _client, HandleLoginAsync, HandleLogout)
        {
            Owner = this,
        };
        win.ShowDialog();

        // 设置里可能改了/取消了快捷键，回来要刷新详情页那张表
        RefreshShortcuts();
    }

    /// <summary>
    /// 详情页的快捷键提示：只显示当前真正有绑定的，取消掉的自动隐藏。
    /// </summary>
    private void RefreshShortcuts()
    {
        var rows = KeyMap.Actions
            .Where(def => !def.Basic)              // 上下左右这些基础键不列
            .Select(def => new { def, key = _keyMap.Get(def.Id) })
            .Where(x => x.key != Key.None)         // 取消掉的不列
            .Select(x => new { Key = KeyMap.KeyText(x.key), Action = x.def.Label })
            .ToList();

        ShortcutList.ItemsSource = rows;
        ApplyShortcutVisibility();
    }

    private void OnToggleShortcuts(object sender, RoutedEventArgs e)
    {
        _settings.ShowShortcuts = !_settings.ShowShortcuts;
        _settings.Save();
        ApplyShortcutVisibility();
    }

    private void ApplyShortcutVisibility()
    {
        ShortcutList.Visibility = _settings.ShowShortcuts
            ? Visibility.Visible : Visibility.Collapsed;
        ShortcutToggle.Text = _settings.ShowShortcuts ? "快捷键 ▾" : "快捷键 ▸";
    }

    /// <summary>扫码登录。返回是否真的登上了（取消或失败返回 false）。</summary>
    private async Task<bool> HandleLoginAsync()
    {
        var win = new LoginWindow { Owner = this };
        win.ShowDialog();

        if (string.IsNullOrEmpty(win.Cookies))
        {
            SetStatus("已取消登录");
            return false;
        }

        var session = await _client.EnsureSessionAsync();
        session.LoginCookies = win.Cookies;
        session.Save();

        SetStatus("登录成功，正在刷新推荐…");
        await LoadFeedAsync(reset: true);
        return true;
    }

    private void HandleLogout()
    {
        var session = _client.Session;
        if (session is null) return;

        session.LoginCookies = "";
        session.Save();
        SetStatus("已退出登录");
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_adjustingState) return;

        // 双击标题栏时 WindowChrome 会把窗口设成 Maximized（会盖住任务栏），
        // 拦下来转成自定义最大化
        if (WindowState == WindowState.Maximized)
        {
            _adjustingState = true;
            WindowState = WindowState.Normal;
            _adjustingState = false;

            if (!_maximized) ToggleMaximize();
            return;
        }

        LayoutVideo();
    }

    // ================= 收尾 =================

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _prefetchCts?.Cancel();
        try { await _proxy.DisposeAsync(); } catch { /* 收尾不抛 */ }
        if (_mpv is null) return;

        // mpv 是独立进程，不显式 quit 会留在后台
        e.Cancel = true;
        var mpv = _mpv;
        _mpv = null;
        try { await mpv.DisposeAsync(); } catch { /* 忽略 */ }
        Close();
    }

    // ================= 工具 =================

    private static string Fmt(double sec)
    {
        if (double.IsNaN(sec) || sec < 0) sec = 0;
        int s = (int)sec;
        return s >= 3600
            ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}"
            : $"{s / 60}:{s % 60:00}";
    }

    private static string Count(long n) => n switch
    {
        >= 100_000_000 => (n / 100_000_000.0).ToString("0.#") + "亿",
        >= 10_000 => (n / 10_000.0).ToString("0.#") + "万",
        _ => n.ToString(),
    };
}

/// <summary>找 mpv.exe：先看环境变量，再从程序目录往上找 assets/mpv。</summary>
internal static class MpvLocator
{
    public static string Find()
    {
        string? env = Environment.GetEnvironmentVariable("DY_MPV");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "assets", "mpv", "mpv.exe");
            if (File.Exists(p)) return p;
        }

        throw new FileNotFoundException(
            "找不到 mpv.exe。在项目根目录运行 scripts\\fetch-mpv.ps1 下载，或设置环境变量 DY_MPV 指向它。");
    }
}
