using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DouyinDesktop.Core;
using DouyinDesktop.Core.Api;
using DouyinDesktop.Core.Models;
using DouyinDesktop.Player;

namespace DouyinDesktop.App;

public partial class MainWindow : Window
{
    private readonly DouyinClient _client = DouyinClient.Create();
    private readonly VideoStreamProxy _proxy = VideoStreamProxy.Start(cache: PrefetchCache.TryOpen());

    /// <summary>这次启动是「接着上次看」恢复出来的，还没成功播过一条。</summary>
    private bool _restoredSession;
    private DateTime _lastSessionSave = DateTime.MinValue;
    private int _sessionSaving;
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

    // ================= 弹幕 =================
    private int _danmakuSubId = -1;
    private DanmakuOverlay? _overlay;
    private string _danmakuAwemeId = "";
    private string? _danmakuAssPath;
    private long _danmakuLoadedTo;          // 已经拉到第几毫秒
    private bool _danmakuBusy;
    private CancellationTokenSource? _danmakuCts;
    private readonly Dictionary<string, DanmakuItem> _danmakuItems = new();

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
        // 强校验接口走「浏览器模板」：平时不碰浏览器，模板缺失/失效才抓一次
        _client.GetTemplate = ApiTemplate.Get;
        _client.CaptureTemplate = id => TemplateFetcher.CaptureAsync(id);
        _client.InvalidateTemplate = ApiTemplate.Invalidate;
        _healthHandler = () => Dispatcher.BeginInvoke(UpdateExpiredBadge);
        LoginHealth.Changed += _healthHandler;
        Closed += (_, _) => LoginHealth.Changed -= _healthHandler;
        Surface.HandleCreated += OnSurfaceReady;

        // 进度条：250ms 刷一次就够了，太频繁反而白耗 CPU
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _progressTimer.Start();

        // 每 10 分钟从内置浏览器同步一次 Cookie。
        //
        // 为什么要做：我们保存的是登录那一刻的快照，而抖音会在浏览器里
        // 不断刷新登录凭据。不同步的话，快照慢慢失效，表现就是"登录老是掉"。
        // 只读 Cookie、不加载页面，1–2 秒，且只在空闲时做。
        _cookieSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _cookieSyncTimer.Tick += async (_, _) => await SyncCookiesQuietlyAsync();
        _cookieSyncTimer.Start();
    }

    private DispatcherTimer? _cookieSyncTimer;

    /// <summary>安静地同步一次浏览器 Cookie（不打扰用户、不弹窗、不阻塞操作）。</summary>
    private async Task SyncCookiesQuietlyAsync()
    {
        var session = _client.Session;
        if (session?.IsLoggedIn != true) return;
        if (JustSwitched) return;                    // 正在刷视频，别插一脚

        try
        {
            string? cookies = await WebCookieSync.ReadAsync();
            if (string.IsNullOrEmpty(cookies) || !cookies.Contains("sessionid=")) return;
            if (cookies == session.LoginCookies) return;

            session.LoginCookies = cookies;
            session.Save();
            AppLog.Write("已自动同步浏览器 Cookie（保持登录有效）");
        }
        catch (Exception ex)
        {
            AppLog.Write("自动同步 Cookie 失败：" + ex.Message);
        }
    }

    private DispatcherTimer? _progressTimer;
    private Action? _healthHandler;

    /// <summary>右下角的登录过期提醒：只有判定过期才显示，点一下直接去登录。</summary>
    private void UpdateExpiredBadge()
    {
        if (ExpiredBadge is null) return;
        ExpiredBadge.Visibility = LoginHealth.Current == LoginHealth.State.Expired
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnExpiredBadgeClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _ = ReloginFreshAsync();
    }

    /// <summary>
    /// 判定过期后的重新登录：**先把内置浏览器的登录状态清掉**再弹扫码。
    ///
    /// 不清的话，登录窗口一打开页面就是"已登录"→ 窗口一闪就关 →
    /// 抓回来还是那份过期 Cookie，等于没重新登录（用户踩过两次）。
    /// </summary>
    private async Task ReloginFreshAsync()
    {
        // 先试着从浏览器同步：内置浏览器里可能还登录着，
        // 能同步成功就**不用扫码**（扫码很麻烦，能不扫就不扫）。
        _browserSyncTried = false;
        SetStatus("正在尝试用浏览器里的登录状态恢复…");

        if (await TryRefreshFromBrowserAsync())
        {
            string id = CurrentId;
            if (!string.IsNullOrEmpty(id))
            {
                var page = await _client.GetCommentsAsync(id, 0, 3);
                if (page.Comments.Count > 0)
                {
                    LoginHealth.Set(LoginHealth.State.Ok);
                    SetStatus("已用浏览器里的登录状态恢复，无需重新扫码");
                    return;
                }
            }
        }

        // 同步不回来才走扫码
        SetStatus("正在清理旧的登录状态…");
        ClearWebViewProfile();

        var session = _client.Session;
        if (session is not null)
        {
            session.LoginCookies = "";
            session.Uifid = "";
            session.Save();
        }

        _browserSyncTried = false;
        LoginHealth.Set(LoginHealth.State.Guest);
        await HandleLoginAsync();
    }
    /// <summary>按当前播放位置画底部进度条。时长优先取作品数据，拿不到就用 0（不画）。</summary>
    private void UpdateProgress()
    {
        if (ProgressFill is null || ProgressTrack is null) return;

        // 顺手同步登录过期徽章：不依赖事件，避免"状态改了但界面没反应"
        UpdateExpiredBadge();

        double pos = _mpv?.Position ?? 0;
        double total = _mpv?.Duration ?? 0;

        if (total <= 0 || pos <= 0 || _imageMode)
        {
            ProgressFill.Width = 0;
            return;
        }

        double ratio = Math.Clamp(pos / total, 0, 1);
        double track = ProgressTrack.ActualWidth;
        ProgressFill.Width = track <= 0 ? 0 : track * ratio;
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

        // 调试用：--aweme=<id> 只播这一条（验证弹幕这类要看特定作品的场景）
        string? awemeArg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--aweme=", StringComparison.Ordinal));
        if (awemeArg is not null)
        {
            _ = LoadSingleAwemeAsync(awemeArg["--aweme=".Length..]);
            return;
        }

        // 先试着接着上次看：一次请求都不用，直接开播。
        // 恢复这条路再出问题也不能影响启动——兜底就是老老实实拉推荐。
        bool restored = false;
        try
        {
            restored = await TryRestoreLastSessionAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("恢复上次失败：" + ex.Message);
            LastSession.Clear();
        }
        if (restored) return;

        // 播放器和推荐流并行准备：谁先好都不等谁
        _ = LoadFeedAsync();
        await Task.CompletedTask;
    }

    /// <summary>
    /// 恢复上次退出时的现场：列表 + 位置 + 那条视频。
    ///
    /// 两个前提：
    ///   1. 保存时间在 48 小时内（再久推荐内容就太陈旧了）
    ///   2. 存下来的播放地址**还有效**——抖音的播放地址带鉴权参数，过几小时会失效。
    ///      这里用一个 1KB 的 Range 请求探一下，失效就老老实实重新拉推荐。
    ///
    /// 恢复成功后不主动拉新推荐：列表里还有几十条，够看一阵。
    /// 等翻到快见底时 EnsureBuffer 会自动补新的一批，不用额外逻辑。
    /// </summary>
    private async Task<bool> TryRestoreLastSessionAsync()
    {
        var snap = LastSession.Load();
        if (snap is null) return false;

        int idx = Math.Clamp(snap.Index, 0, snap.Items.Count - 1);
        var item = snap.Items[idx];

        _items.Clear();
        _presented.Clear();
        foreach (var a in snap.Items)
            if (_presented.Add(a.Id)) _items.Add(a);
        if (_items.Count == 0) return false;

        int saved = _items.FindIndex(x => x.Id == item.Id);
        if (saved < 0) saved = 0;

        // 索引存错的问题已经在 LastSession 里修掉了（原来是按整表长度夹，
        // 翻过 30 条后永远读成第 30 条），这里就老老实实接着上次那条播。
        int start = saved;

        var play = _items[start];
        string playUri = play.Video?.PlayAddr?.Uri ?? "";
        if (playUri.Length == 0) return false;

        SetStatus("接着上次继续…");
        var probe = await _client.ProbeStreamAsync(playUri);
        if (!probe.Ok)
        {
            AppLog.Write($"上次的播放地址已失效（{probe.Detail}），重新拉推荐");
            LastSession.Clear();
            return false;
        }

        _index = start;
        _restoredSession = true;
        AppLog.Write($"接着上次：{_items.Count} 条，从第 {start + 1} 条开始（上次第 {saved + 1} 条），" +
                     $"探活 {probe.ElapsedMs}ms");

        await PlayIndexAsync(_index, force: true);
        return true;
    }

    /// <summary>
    /// 调试入口：--aweme=&lt;id&gt; 只播这一条。
    /// 验证弹幕这类要看特定作品的场景时用，不然每次首条都是随机推荐。
    /// </summary>
    private async Task LoadSingleAwemeAsync(string awemeId)
    {
        SetStatus("正在取作品 " + awemeId + "…");
        var item = await _client.GetAwemeDetailAsync(awemeId);
        if (item is null)
        {
            SetStatus("取作品失败：" + awemeId);
            return;
        }

        _items.Clear();
        _presented.Clear();
        _items.Add(item);
        _index = 0;
        AppLog.Write($"单条调试：{awemeId} @{item.AuthorName}");
        await PlayIndexAsync(0, force: true);
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

                // 关注流拿到空内容 = 服务端没把我们当登录用户（设备身份和 Cookie 对不上）。
                // 但快速连切时也会空（请求太密被挡），所以只在停下来之后才计入。
                if (r.Items.Count == 0)
                {
                    if (JustSwitched)
                    {
                        AppLog.Write("[关注流] 空内容（连切中，不计入）");
                    }
                    else
                    {
                        _followEmptyStreak++;
                        AppLog.Write($"[关注流] 空内容（连续 {_followEmptyStreak} 次）");
                        if (_followEmptyStreak >= 3)
                            LoginHealth.Set(LoginHealth.State.Expired);
                    }
                }
                else
                {
                    _followEmptyStreak = 0;
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
    /// 退出作者流：回推荐流 + 右侧切回「详情」页。
    /// U 和 Esc 共用——U 是开关，Esc 是"退一层"的直觉操作。
    /// 推荐流走缓存，不重新请求，位置还是离开时那一条。
    /// </summary>
    private async Task LeaveAuthorAsync()
    {
        _authorMode = false;
        _followMode = false;
        await SwitchFeedAsync(_recommendFeed, "推荐");
        SwitchTab(SideTab.Detail);
        SetStatus("已回到推荐");
    }

    // ================= 弹幕 =================

    /// <summary>
    /// 换作品时重置弹幕：摘掉上一条的字幕轨，重新开始拉。
    /// </summary>
    private void ResetDanmaku(Aweme item)
    {
        _danmakuCts?.Cancel();
        _danmakuCts?.Dispose();
        _danmakuCts = null;

        _danmakuItems.Clear();
        _danmakuLoadedTo = 0;
        _danmakuAwemeId = item.Id;
        _danmakuAssPath = null;

        int old = _danmakuSubId;
        _danmakuSubId = -1;
        if (old >= 0) _ = RemoveSubtitleQuietlyAsync(old);

        _overlay?.Clear();

        if (!_settings.Danmaku || item.IsImagePost) return;

        var cts = new CancellationTokenSource();
        _danmakuCts = cts;

        // 上一条的拉取任务可能还在收尾，它会把 _danmakuBusy 占着，
        // 新的这条就会直接 return —— 所以这里先清掉
        _danmakuBusy = false;
        _ = PumpDanmakuAsync(item, cts);
    }

    private async Task RemoveSubtitleQuietlyAsync(int id)
    {
        try { if (_mpv is not null) await _mpv.RemoveSubtitleAsync(id); }
        catch { /* 轨道可能已经跟着上一条视频没了 */ }
    }

    /// <summary>
    /// 一次拉一个 32 秒窗口，始终保持比播放位置超前 64 秒。
    /// 接口是分段返回的，只能边播边拉，不能一次要全部。
    /// </summary>
    private async Task PumpDanmakuAsync(Aweme item, CancellationTokenSource cts)
    {
        if (_danmakuBusy) return;
        _danmakuBusy = true;
        var ct = cts.Token;
        try
        {
            // 等真正开播再拉弹幕：切换瞬间视频还在建连接/缓冲，
            // 这时候连发三个弹幕请求会抢带宽，起播明显变慢。
            int waited = 0;
            while (!ct.IsCancellationRequested && (_mpv?.Position ?? 0) <= 0.05 && waited < 4000)
            {
                await Task.Delay(200, ct);
                waited += 200;
            }

            while (!ct.IsCancellationRequested)
            {
                double posSec = _mpv?.Position ?? 0;
                if (_danmakuLoadedTo >= (long)(posSec * 1000) + 64_000) break;

                long start = _danmakuLoadedTo;
                var page = await _client.GetDanmakuAsync(item.Id, start, start + 32_000, ct);
                if (ct.IsCancellationRequested) return;
                if (page.EndMs <= start) break;      // 服务端没给有效窗口，别死循环

                foreach (var d in page.Items) _danmakuItems[d.Id] = d;
                _danmakuLoadedTo = page.EndMs;

                if (_danmakuItems.Count > 0) await ApplyDanmakuAsync(item, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("弹幕失败：" + ex.Message); }
        finally
        {
            // 只有「还是当前这一轮」才解锁，避免旧任务把新任务的闸门打开
            if (ReferenceEquals(cts, _danmakuCts)) _danmakuBusy = false;
        }
    }

    /// <summary>把攒到的弹幕写到画面上：默认走浮层，也可以切回 mpv 字幕轨。</summary>
    private async Task ApplyDanmakuAsync(Aweme item, CancellationToken ct)
    {
        if (_mpv is null) return;

        var style = _settings.ToDanmakuStyle();

        if (_settings.DanmakuRenderer == "overlay")
        {
            if (_overlay is null)
            {
                _overlay = new DanmakuOverlay(this, VideoHost,
                    () => _mpv?.Position ?? 0,
                    () => _mpv?.IsPaused ?? false);
                _overlay.Show();
            }

            // 浮层接管后，字幕轨那条路要清干净，否则两套会叠在一起
            int old = _danmakuSubId;
            _danmakuSubId = -1;
            _danmakuAssPath = null;
            if (old >= 0) await RemoveSubtitleQuietlyAsync(old);

            _overlay.SetVideoSize(item.Video?.Width ?? 0, item.Video?.Height ?? 0);
            _overlay.SetStyle(style);
            _overlay.SetItems(_danmakuItems.Values);
            AppLog.Write($"弹幕浮层：{_danmakuItems.Count} 条 / 区域 {_settings.DanmakuArea}");
            return;
        }

        // 切回字幕轨时，之前开过的浮层要收掉，不然两套叠在一起
        _overlay?.Clear();

        string ass = DanmakuAss.Build(
            _danmakuItems.Values.ToList(),
            item.Video?.Width ?? 0, item.Video?.Height ?? 0,
            style);

        string dir = Path.Combine(DouyinPaths.DataDirectory, "danmaku");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, item.Id + ".ass");
        await File.WriteAllTextAsync(path, ass, ct);
        if (ct.IsCancellationRequested) return;

        if (_danmakuSubId < 0 || _danmakuAssPath != path)
        {
            int old = _danmakuSubId;
            if (old >= 0) await RemoveSubtitleQuietlyAsync(old);

            _danmakuSubId = await _mpv.AddSubtitleAsync(path);
            _danmakuAssPath = path;
            AppLog.Write($"弹幕挂上：{_danmakuItems.Count} 条，轨道 {_danmakuSubId}");
        }
        else
        {
            await _mpv.ReloadSubtitleAsync();
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
                  "，再按 U 回推荐");
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
            ResetDanmaku(item);              // 图集没有弹幕，顺手把上一条的字幕轨摘掉
            ShowInfo(item);
            await ShowImagePostAsync(item);
            UpdateIndexLabel();
            PrefetchAround(index);
            EnsureBuffer();
            SaveSessionIfNeeded();
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
            // "loading failed" 基本都是播放地址过期了（列表里放了一会儿的老条目）。
            // 重新取一次详情换上新地址，再试一次——不行才报错。
            AppLog.Write($"加载失败（{ex.Message}），尝试刷新播放地址：{item.Id}");
            var fresh = await RefreshPlayUrlAsync(item);
            if (fresh is not null)
            {
                try
                {
                    await _mpv.LoadAsync(_proxy.UrlFor(fresh));
                    item = fresh;
                }
                catch (Exception ex2)
                {
                    SetStatus("加载失败：" + ex2.Message);
                }
            }
            else
            {
                SetStatus("加载失败：" + ex.Message);
            }
        }

        ResetDanmaku(item);
        _lastFromCache = _proxy.HasPrefetch(item.Id);
        PrefetchAround(index);
        UpdateIndexLabel();
        EnsureBuffer();
        SaveSessionIfNeeded();
    }

    /// <summary>已经为重试刷过地址的作品，避免同一个 id 无限重试。</summary>
    private readonly HashSet<string> _refreshTried = new();

    /// <summary>重新取一次详情换上新播放地址（老条目的地址会过期）。</summary>
    private async Task<Aweme?> RefreshPlayUrlAsync(Aweme item)
    {
        try
        {
            var fresh = await _client.GetAwemeDetailAsync(item.Id);
            if (fresh?.Video?.PlayAddr?.Uri is not { Length: > 0 }) return null;

            int at = _items.FindIndex(x => x.Id == item.Id);
            if (at >= 0) _items[at] = fresh;

            AppLog.Write($"播放地址已刷新：{item.Id}");
            return fresh;
        }
        catch (Exception ex)
        {
            AppLog.Write("刷新播放地址失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 切视频的节流：连按方向键时，上一条还没起播就跳下一条，
    /// 会出现"点了几下只前进一格"或者干脆加载失败。300ms 内只认一次。
    /// </summary>
    private DateTime _lastSwitchAt = DateTime.MinValue;

    private bool SwitchThrottled()
    {
        var now = DateTime.UtcNow;
        if (now - _lastSwitchAt < TimeSpan.FromMilliseconds(300)) return true;
        _lastSwitchAt = now;
        return false;
    }

    /// <summary>
    /// 用默认浏览器打开当前作品的网页版。
    ///
    /// 抖音收紧了接口签名后，评论、作者作品、点赞在客户端里都拿不到/发不出去，
    /// 这条路 0 开销、0 维护——需要评论或点赞时按 B 切过去看。
    /// </summary>
    private void OpenInBrowser()
    {
        if (_index < 0 || _index >= _items.Count)
        {
            SetStatus("还没有在播的作品");
            return;
        }

        string url = $"https://www.douyin.com/video/{_items[_index].Id}";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            SetStatus("已在浏览器中打开");
        }
        catch (Exception ex)
        {
            SetStatus("打开浏览器失败：" + ex.Message);
        }
    }

    // ================= 点赞 =================

    /// <summary>本次运行里点过赞的作品（详情接口不保证每次都带 user_digged）。</summary>
    private readonly HashSet<string> _liked = new();
    private bool _likeBusy;

    private void OnLikeClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _ = ToggleLikeAsync();
    }

    private async Task ToggleLikeAsync()
    {
        if (_index < 0 || _index >= _items.Count) return;
        if (_likeBusy) return;

        var item = _items[_index];

        if (_client.Session?.IsLoggedIn != true)
        {
            SetStatus("点赞需要先登录（Ctrl+O → 账号）");
            return;
        }

        _likeBusy = true;
        try
        {
            bool like = !_liked.Contains(item.Id);
            if (!await _client.DiggAsync(item.Id, like))
            {
                SetStatus("点赞失败（可能是登录态失效）");
                return;
            }

            // status_code=0 不等于真的生效，所以尽量回读一次详情看 user_digged。
            // 但详情接口经常拿不到（风控）或者不带这个字段 —— 那就**不要下结论**，
            // 之前一律当成失败，导致明明点上了也显示"没有生效"。
            var check = await _client.GetAwemeDetailAsync(item.Id);
            int? actual = check?.UserDigged;

            if (actual is not null && (actual == 1) != like)
            {
                SetStatus(like ? "点赞没有生效（可能被风控）" : "取消点赞没有生效");
                return;
            }

            if (like) _liked.Add(item.Id); else _liked.Remove(item.Id);
            UpdateLikeVisual(item);
            SetStatus(like ? "已点赞" : "已取消点赞");
        }
        finally { _likeBusy = false; }
    }

    private void UpdateLikeVisual(Aweme item)
    {
        bool liked = _liked.Contains(item.Id);
        long n = item.Statistics?.DiggCount ?? 0;
        if (liked) n++;     // 自己点的那一下算进去，数字才有反馈
        LikeText.Text = $"{(liked ? "♥" : "♡")} {Count(n)}";
        LikeText.Foreground = liked
            ? new SolidColorBrush(Color.FromRgb(0xFE, 0x2C, 0x55))
            : (Brush)FindResource("TextBrush");
    }

    /// <summary>
    /// 把当前现场存下来，下次启动接着看。
    /// 只记推荐流（关注/作者流是临时的，下次也没法原样恢复），
    /// 2 秒内只写一次——连按切换时不必每条都落盘。
    /// </summary>
    private void SaveSessionIfNeeded()
    {
        if (_authorMode || _followMode) return;
        if (_index < 0 || _items.Count == 0) return;
        if ((DateTime.UtcNow - _lastSessionSave).TotalSeconds < 2) return;

        // 已经在写了就别排第二个（序列化 30 条要几毫秒，扔后台线程不挡切换）
        if (Interlocked.Exchange(ref _sessionSaving, 1) == 1) return;

        _lastSessionSave = DateTime.UtcNow;
        int idx = _index;
        var snapshot = _items.ToList();     // 快照：后台线程不能读正在变的列表

        _ = Task.Run(() =>
        {
            try { LastSession.Save(idx, snapshot); }
            finally { Interlocked.Exchange(ref _sessionSaving, 0); }
        });
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

        // 刚被限流过：直接告诉用户稍后再试，别继续撞接口
        if (reset && DateTime.Now < _commentCooldownUntil)
        {
            CommentEmpty.Text = "评论暂时取不到（请求太频繁），稍等一会儿再切回来";
            CommentEmpty.Visibility = Visibility.Visible;
            return;
        }

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

            if (_commentVms.Count == 0)
            {
                // 空响应先按"限流"处理：冷却一段时间，别马上重试
                if (reset) _commentCooldownUntil = DateTime.Now.AddSeconds(90);
                CommentEmpty.Text = page.Total == 0 ? "暂无评论" : "评论暂时取不到（请求太频繁），稍后再试";
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

        AppLog.Write($"[主页] 请求 {author.Nickname} / {author.SecUid}（当前已显示 {_profileSecUid}）");

        // 同一个作者不重复拉
        if (_profileSecUid == author.SecUid && ProfileList.ItemsSource is not null)
        {
            AppLog.Write("[主页] 同作者，跳过");
            return;
        }

        if (_profileCache.TryGetValue(author.SecUid, out var cached))
        {
            AppLog.Write($"[主页] 命中缓存 {cached.Count} 条");
            ShowProfile(author, cached);
            return;
        }

        // 换作者时先把上一位的作品清掉，否则加载期间会看到别人的视频
        ProfileList.ItemsSource = null;
        ProfileName.Text = "@" + author.Nickname;
        ProfileAvatar.Source = null;
        ProfileCount.Text = "加载中…";

        ProfileCount.Text = "加载中…";
        var result = await _client.GetUserPostsAsync(author.SecUid);
        if (!result.Success)
        {
            ProfileCount.Text = "";
            AppLog.Write("[主页] 失败：" + result.Message);
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

    /// <summary>
    /// 切换节流用的时间戳。
    ///
    /// 播放失败时会「刷新地址重试 → 跳过下一条」，而 mpv 的 end-file 事件也是异步的，
    /// 两个来源叠在一起会把 NextAsync 连续触发好几次
    /// （日志里 1 秒内 loadfile 六七次 → 用户看到"视频疯狂跳、然后全变大众视频"）。
    /// 这里用时间戳限速，不用布尔锁——布尔锁一旦卡住就再也切不了。
    /// </summary>
    private DateTime _lastNextAt = DateTime.MinValue;

    private async Task NextAsync()
    {
        if (_items.Count == 0) return;

        // 用时间戳节流，**不要用布尔锁**：
        // 播放过程里可能有网络等待，一旦卡住布尔锁就永远释放不了，表现就是"切换不了"。
        DateTime now = DateTime.Now;
        if (now - _lastNextAt < TimeSpan.FromMilliseconds(250)) return;
        _lastNextAt = now;

        if (_index + 1 < _items.Count)
        {
            await PlayIndexAsync(_index + 1);
            return;
        }

        // 已经到缓冲区末尾：等下一批
        SetStatus("正在加载更多…");
        await LoadFeedAsync();
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
        _restoredSession = false;      // 恢复的那条播起来了，之后就不算"恢复中"了
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
            string id = _index >= 0 && _index < _items.Count ? _items[_index].Id : "?";

            // mpv 的加载失败是**异步**的 end-file，不是 LoadAsync 抛异常，
            // 所以刷新地址的重试必须挂在这里。每条只重试一次，避免死循环。
            if (_index >= 0 && _index < _items.Count && _refreshTried.Add(id))
            {
                AppLog.Write($"加载失败（{reason}），刷新地址重试：{id}");
                var fresh = await RefreshPlayUrlAsync(_items[_index]);
                if (fresh is not null)
                {
                    await PlayIndexAsync(_index, force: true);
                    return;
                }
            }

            _consecutiveFailures++;
            AppLog.Write($"加载失败 #{_consecutiveFailures} {id}: {reason}");
            SetStatus($"加载失败：{reason}");

            // 恢复出来的第一条就播不动：多半是整批地址都过期了，直接换一批新的
            if (_restoredSession)
            {
                _restoredSession = false;
                AppLog.Write("恢复的地址整批失效，重新拉推荐");
                SetStatus("上次的地址失效了，重新拉推荐…");
                await LoadFeedAsync(reset: true);
                return;
            }

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

            // 快播到已拉取的边缘了，接着往后拉一段（每 32 秒一次）
            if (_settings.Danmaku && !_danmakuBusy && _danmakuCts is { IsCancellationRequested: false } cts
                && _index >= 0 && _index < _items.Count
                && _items[_index].Id == _danmakuAwemeId
                && (long)(pos * 1000) + 32_000 > _danmakuLoadedTo)
            {
                _ = PumpDanmakuAsync(_items[_index], cts);
            }
        });
    }

    private void OnDuration(double dur)
    {
        Dispatcher.BeginInvoke(() => TimeText.Text = $"{Fmt(_mpv?.Position ?? 0)} / {Fmt(dur)}");
        _ = CheckLoginHealthAsync();
    }

    /// <summary>关注流连续空内容的次数（用来判断"登录态没被服务端认"）。</summary>
    private int _followEmptyStreak;

    /// <summary>
    /// 最近是否在快速切视频。
    ///
    /// ⚠️ 必须和 SwitchThrottled 用**同一个时间基准（UtcNow）**。
    /// 之前这里用 DateTime.Now（本地时间，比 UTC 快 8 小时），
    /// 和 UTC 时间戳相减得到负数 → 永远"刚切过" → 切换被永久拦死。
    /// </summary>
    private bool JustSwitched => (DateTime.UtcNow - _lastSwitchAt).TotalSeconds < 3;

    private DateTime _lastHealthCheck = DateTime.MinValue;
    private bool _browserSyncTried;

    /// <summary>
    /// 评论接口的冷却期。
    ///
    /// 抖音对评论请求限流很紧：连续几次就返回空 body（实测 14 秒内就被挡）。
    /// 空响应不能当成"登录过期"，也不该马上重试——越试挡得越久。
    /// </summary>
    private DateTime _commentCooldownUntil = DateTime.MinValue;

    /// <summary>
    /// 从内置浏览器同步一次登录凭据（浏览器那份会自己续期）。
    /// 每次运行最多试一次，避免反复起浏览器内核。
    /// </summary>
    private async Task<bool> TryRefreshFromBrowserAsync()
    {
        if (_browserSyncTried) return false;
        _browserSyncTried = true;

        var session = _client.Session;
        if (session is null) return false;

        SetStatus("正在从浏览器同步登录状态…");
        string? cookies = await WebCookieSync.ReadAsync();

        if (string.IsNullOrEmpty(cookies) || !cookies.Contains("sessionid="))
        {
            AppLog.Write("浏览器里没有可用的登录状态");
            SetStatus("浏览器里也没有登录状态，需要重新扫码");
            return false;
        }

        if (cookies == session.LoginCookies) return false;

        session.LoginCookies = cookies;
        session.Save();
        AppLog.Write("已从浏览器同步登录状态（" + SessionExpiry.Describe(cookies) + "）");
        return true;
    }

    /// <summary>
    /// 视频加载好后在后台拉一小页评论，用来判断登录态是否还有效。
    ///
    /// 抖音对过期登录态只返回空 body、不给错误码，所以"能不能读到评论"
    /// 是唯一可靠的判据。结果写到 LoginHealth，设置页和右下角读它显示。
    ///
    /// 什么时候会去起浏览器同步：只在这两种情况下 ——
    ///   1. 凭据快过期（不到 12 小时）
    ///   2. 评论读不到、判定已过期
    /// 平时启动**不会**碰浏览器。
    /// </summary>
    private async Task CheckLoginHealthAsync()
    {
        // 只在"还不知道状态"或"已经判定过期"时才查。
        // 状态正常就不查了——评论接口被频繁调用会被抖音短时限流，
        // 表现就是"刚登录能看，刷几条之后全空"，很容易被误判成登录过期。
        if (LoginHealth.Current == LoginHealth.State.Ok) return;
        if (JustSwitched) return;                            // 刚连切完，别下结论
        if (DateTime.Now < _commentCooldownUntil) return;   // 刚被限流过，别再撞

        // 正常状态 30 分钟才查一次；已经出现过一次空，就 20 秒后复查确认
        TimeSpan gap = _emptyStreak > 0 ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(30);
        if (DateTime.Now - _lastHealthCheck < gap) return;

        var session = _client.Session;
        if (session?.IsLoggedIn != true)
        {
            LoginHealth.Set(LoginHealth.State.Guest);
            return;
        }

        string id = CurrentId;
        if (string.IsNullOrEmpty(id)) return;

        _lastHealthCheck = DateTime.Now;

        _lastHealthCheck = DateTime.Now;

        // 快过期就先同步，省得等它真的失效
        if (!_browserSyncTried
            && SessionExpiry.IsNearExpiry(session.LoginCookies, TimeSpan.FromHours(12)))
        {
            await TryRefreshFromBrowserAsync();
        }

        try
        {
            var page = await _client.GetCommentsAsync(id, 0, 5);

            if (page.Comments.Count > 0)
            {
                _emptyStreak = 0;
                AppLog.Write($"[登录检查] {id} 评论 {page.Comments.Count} 条 → 正常");
                LoginHealth.Set(LoginHealth.State.Ok);
                return;
            }

            // 零评论的视频也会返回空，所以不能一次就判定过期：
            // 连续两条都空才算（宁可晚一点提示，也别误报）。
            _emptyStreak++;
            AppLog.Write($"[登录检查] {id} 评论 0 条（连续 {_emptyStreak} 次）");

            if (_emptyStreak < 2)
            {
                if (LoginHealth.Current == LoginHealth.State.Unknown)
                    LoginHealth.Set(LoginHealth.State.Ok);
                return;
            }

            // 评论空 ≠ 登录失效：评论接口会被单独限流（日志里见过：
            // 同一时刻作者作品能拉 23 条、评论却是 0 条）。
            // 用"作者作品"交叉验证一下，能拉到就说明登录没问题。
            string? secUid = _index >= 0 && _index < _items.Count ? _items[_index].Author?.SecUid : null;
            if (!string.IsNullOrEmpty(secUid))
            {
                var posts = await _client.GetUserPostsAsync(secUid);
                if (posts.Success && posts.Items.Count > 0)
                {
                    AppLog.Write($"[登录检查] 作品能拉到 {posts.Items.Count} 条 → 登录正常，评论属于限流");
                    LoginHealth.Set(LoginHealth.State.Ok);
                    return;
                }
            }

            // 先亮提示，再去后台尝试救回来——不要等浏览器起完才让用户看到
            LoginHealth.Set(LoginHealth.State.Expired);
            AppLog.Write("[登录检查] 判定登录已过期");

            if (await TryRefreshFromBrowserAsync())
            {
                page = await _client.GetCommentsAsync(id, 0, 5);
                bool ok = page.Comments.Count > 0;
                AppLog.Write($"[登录检查] 同步后复查：{(ok ? "正常" : "仍然为空")}");
                if (ok)
                {
                    _emptyStreak = 0;
                    LoginHealth.Set(LoginHealth.State.Ok);
                }
            }
        }
        catch
        {
            // 网络问题不算登录失效，保持原状态
        }
    }

    /// <summary>连续读到空评论的次数（用于区分"零评论视频"和"登录失效"）。</summary>
    private int _emptyStreak;

    // ================= 界面 =================

    /// <summary>
    /// 底衬取画面主色调（不是模糊图——模糊图铺在两边看着很乱）。
    /// 视频的黑边由 mpv 自己画，所以颜色要同时设给 mpv 的 background-color；
    /// 图集的留白是 WPF 画的，设给 BackdropRect。
    /// </summary>
    private async Task UpdateBackdropAsync(Aweme item, ImageInfo? firstImage)
    {
        try
        {
            byte[]? bytes = firstImage is not null
                ? await GetImageBytesAsync(firstImage)
                : await GetBytesAsync(item.Video?.CoverUrl ?? "");

            if (bytes is null || bytes.Length == 0) return;

            var c = Darken(AverageColor(bytes), 0.34);
            BackdropRect.Fill = new SolidColorBrush(c);

            if (_mpv is not null && !item.IsImagePost)
                await _mpv.SetBackgroundColorAsync($"#{c.R:X2}{c.G:X2}{c.B:X2}");
        }
        catch
        {
            // 取色失败就保持深灰底，不影响播放
        }
    }

    private async Task<byte[]?> GetBytesAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (_imageCache.TryGetValue(url, out var cached)) return cached;

        try
        {
            using var resp = await _imageHttp.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0) return null;
            if (_imageCache.Count > 60) _imageCache.Clear();
            _imageCache[url] = bytes;
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>缩到 8px 取平均色。够代表整体色调，又不会被局部亮块带偏。</summary>
    private static Color AverageColor(byte[] bytes)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.DecodePixelWidth = 8;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();

        var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        if (w < 1 || h < 1) return Color.FromRgb(11, 11, 14);

        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);

        long r = 0, g = 0, b = 0;
        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            b += px[i];
            g += px[i + 1];
            r += px[i + 2];
        }

        int n = Math.Max(1, w * h);
        return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private static Color Darken(Color c, double factor)
    {
        byte Ch(byte v) => (byte)Math.Clamp(v * factor, 0, 255);
        return Color.FromRgb(Ch(c.R), Ch(c.G), Ch(c.B));
    }

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
        // 详情接口带了 user_digged 就以此为准，没有就看本次运行里点过没
        if (item.UserDigged == 1) _liked.Add(item.Id);   // null 表示接口没给，按未点赞处理
        UpdateLikeVisual(item);
        CommentText.Text = $"💬 {Count(st?.CommentCount ?? 0)}";
        ShareText.Text = $"↗ {Count(st?.ShareCount ?? 0)}";

        MusicText.Text = item.Music is { Title.Length: > 0 }
            ? $"♪ {item.Music.Title} — {item.Music.Author}"
            : item.IsImagePost ? "♪ 无背景音乐" : "";

        ResetTechStats();

        // 底衬取画面主色调（视频的黑边交给 mpv 染色，图集的留白是 WPF 这块矩形）
        var firstImage = item.IsImagePost && item.Images is { Count: > 0 } ? item.Images[0] : null;
        _ = UpdateBackdropAsync(item, firstImage);
        LayoutVideo();

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

        // 评论 Tab 上不再挂数量（详情页的统计里有）
        _commentAwemeId = "";

        // 换视频后回到顶部，否则长文案还停在上一条的滚动位置
        DetailPane.ScrollToTop();

        // 画布尺寸固定铺满，不随作品宽高比变化——宽高比交给 mpv 做黑边
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

    /// <summary>
    /// 按当前视频的宽高比，把画布摆到 VideoHost 里（留黑边）。
    ///
    /// animate = true 时给宽高加个短缓动。切视频时用它——不同宽高比的作品
    /// 之间直接跳会"咯噔"一下，160ms 的缓动看起来就是滑过去的。
    /// 窗口拉伸时必须是 false，否则拖边框会拖泥带水。
    /// </summary>
    /// <summary>
    /// 画布**固定铺满**整个视频区，宽高比完全交给 mpv（--keepaspect=yes + --panscan=0）。
    ///
    /// 不要改回「按宽高比内接」：那样切换作品时尺寸会变（推荐流里横屏 16:9 和
    /// 竖屏 9:16 混着），无论先变还是后变，总有一边会露出上一条画面被挤压的样子。
    /// 现在尺寸永远不变，mpv 自己在新视频周围画边——边色由 background-color 决定
    /// （取画面主色调），所以不是死黑。
    /// </summary>
    private void LayoutVideo()
    {
        double w = VideoHost.ActualWidth, h = VideoHost.ActualHeight;
        if (w <= 1 || h <= 1) return;

        // 只在尺寸真的变了才赋值（拖动窗口时避免无谓的 SetWindowPos）
        if (Math.Abs(Surface.Width - w) < 0.5 && Math.Abs(Surface.Height - h) < 0.5) return;

        Surface.BeginAnimation(FrameworkElement.WidthProperty, null);
        Surface.BeginAnimation(FrameworkElement.HeightProperty, null);
        Surface.Width = w;
        Surface.Height = h;
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
            // Esc 是「退一层」，按这个顺序：
            //   全屏   -> 退全屏
            //   作者流 -> 回推荐（同时切回详情页）
            //   其它 Tab -> 回详情页
            if (e.Key == Key.Escape)
            {
                if (_fullscreen)
                {
                    ToggleFullscreen();
                    e.Handled = true;
                    return;
                }

                if (_authorMode)
                {
                    // 必须在 await 之前置 Handled：await 一让出，事件就已经返回了
                    e.Handled = true;
                    await LeaveAuthorAsync();
                    return;
                }

                if (_sideTab != SideTab.Detail)
                {
                    e.Handled = true;
                    SwitchTab(SideTab.Detail);
                }
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
                    if (SwitchThrottled()) break;
                    await NextAsync();
                    break;

                case "prev":
                    if (SwitchThrottled()) break;
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
                    // U 是个开关：在作者流里回推荐，否则切到「主页」Tab
                    if (_authorMode) await LeaveAuthorAsync();
                    else SwitchTab(SideTab.Profile);
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

                case "openweb":
                    OpenInBrowser();
                    break;

                case "like":
                    await ToggleLikeAsync();
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
        // 滚轮一格一格地滚会连续触发，节流和键盘共用同一个时间窗，
        // 否则滚一下能跳好几条，上一条还没起播就被顶掉了。
        if (SwitchThrottled())
        {
            e.Handled = true;
            return;
        }

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

        // 弹幕开关可能变了：关掉就摘轨道，开着且已有数据就只重排样式（不重新请求）
        if (_index >= 0 && _index < _items.Count)
        {
            var item = _items[_index];
            if (!_settings.Danmaku || item.IsImagePost) ResetDanmaku(item);
            else if (_danmakuItems.Count > 0) _ = ApplyDanmakuAsync(item, CancellationToken.None);
            else ResetDanmaku(item);
        }
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
        // 判定过期时，必须先把浏览器里的旧登录清掉再弹窗——
        // 否则页面一打开就是"已登录"，窗口一闪就关，抓回来还是过期 Cookie。
        // （设置页的「重新登录」和右下角徽章都走这里）
        if (LoginHealth.Current == LoginHealth.State.Expired)
        {
            ClearWebViewProfile();
            var old = _client.Session;
            if (old is not null)
            {
                old.LoginCookies = "";
                old.Uifid = "";
                old.Save();
            }
            _browserSyncTried = false;
        }

        var win = new LoginWindow { Owner = this };
        win.ShowDialog();

        if (string.IsNullOrEmpty(win.Cookies))
        {
            SetStatus("已取消登录");
            return false;
        }

        var session = await _client.EnsureSessionAsync();
        session.LoginCookies = win.Cookies;

        // 关键：把设备标识**对齐到刚抓到的 Cookie**。
        // 原来只换 LoginCookies，Ttwid / SvWebId 还是登录前的旧值，
        // 于是请求头（Cookie）和 URL 参数（verifyFp/fp）对不上，
        // 服务端比对不一致就静默返回空 —— 表现就是"登录了但评论/主页全空"。
        string ttwid = session.CookieValue("ttwid");
        string svWebId = session.CookieValue("s_v_web_id");
        string uifid = session.CookieValue("UIFID");

        if (ttwid.Length > 0) session.Ttwid = ttwid;
        if (svWebId.Length > 0) session.SvWebId = svWebId;
        if (uifid.Length > 0) session.Uifid = uifid;

        AppLog.Write($"登录后同步设备标识：ttwid {ttwid.Length} 字符，s_v_web_id {svWebId.Length} 字符，UIFID {uifid.Length} 字符");
        session.Save();

        _client.ResetLoginExpiredFlag();
        _browserSyncTried = false;                    // 新凭据，允许以后再同步一次
        _emptyStreak = 0;                             // 空计数必须清零，否则一登录就被判过期
        _commentCooldownUntil = DateTime.MinValue;    // 限流冷却也清掉
        _lastHealthCheck = DateTime.MinValue;
        LoginHealth.Set(LoginHealth.State.Unknown);   // 等下次加载视频时重新检查
        SetStatus("登录成功，正在刷新推荐…");
        await LoadFeedAsync(reset: true);
        return true;
    }

    private void HandleLogout()
    {
        var session = _client.Session;
        if (session is null) return;

        session.LoginCookies = "";
        session.Uifid = "";
        session.Save();

        // 登录窗口用的是持久化的 WebView2 用户目录，不清掉的话
        // 下次点"扫码登录"页面还是登录状态 → 窗口一闪就自动关掉，根本没机会扫码。
        ClearWebViewProfile();
        SetStatus("已退出登录");
    }

    /// <summary>清掉内置浏览器的登录状态（退出登录时用）。</summary>
    private static void ClearWebViewProfile()
    {
        try
        {
            string dir = DouyinDesktop.Core.DouyinPaths.WebViewDirectory;
            if (!Directory.Exists(dir)) return;

            // Cookie / 缓存都在这些子目录里；被占用时删不掉，忽略即可
            foreach (string sub in new[] { "EBWebView", "Default" })
            {
                string path = Path.Combine(dir, sub);
                if (!Directory.Exists(path)) continue;
                try { Directory.Delete(path, recursive: true); }
                catch (Exception ex) { AppLog.Write($"清理浏览器数据失败（{sub}）：{ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("清理浏览器数据失败：" + ex.Message);
        }
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
        _danmakuCts?.Cancel();
        try { _overlay?.Close(); } catch { /* 已经关了 */ }

        // 退出前把现场存准（绕过 2 秒节流）
        if (!_authorMode && !_followMode && _items.Count > 0 && _index >= 0)
            LastSession.Save(_index, _items);

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
