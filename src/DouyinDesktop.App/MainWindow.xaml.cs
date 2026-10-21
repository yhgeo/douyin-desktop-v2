using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
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
    private readonly List<Aweme> _items = new();
    private readonly HashSet<string> _presented = new();

    // 图集：图片走单独的 HttpClient，缓存最近看过的几十张
    private readonly HttpClient _imageHttp = CreateImageHttp();
    private readonly Dictionary<string, byte[]> _imageCache = new();
    private bool _imageMode;
    private Aweme? _imageItem;
    private int _imageIndex;

    private MpvController? _mpv;
    private int _index = -1;
    private bool _mpvStarting;
    private bool _feedLoading;
    private bool _fullscreen;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private Rect _boundsBeforeFullscreen;
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

    public MainWindow()
    {
        InitializeComponent();
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

    [System.Runtime.InteropServices.DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hImc);

    // ================= 启动 =================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LayoutVideo();
        SetStatus("正在获取推荐…");

        InjectDemoImageIfRequested();

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
            var result = await _client.GetFeedAsync(15, _presented);
            if (!result.Success)
            {
                SetStatus("获取失败：" + result.Message);
                return;
            }

            foreach (var a in result.Items)
            {
                if (_presented.Add(a.Id)) _items.Add(a);
            }

            PushStatus($"推荐流 +{result.Items.Count} 条 / {result.ElapsedMs}ms");
            UpdateLoginLabel();

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
        _items.Clear();
        _items.AddRange(result.Items);
        _index = -1;

        int imageCount = result.Items.Count(x => x.IsImagePost);
        AppLog.Write($"作者作品：{result.Items.Count} 条，其中图集 {imageCount} 条");
        UpdateLoginLabel();
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

    private string CurrentId
        => _index >= 0 && _index < _items.Count ? _items[_index].Id : "";

    private void OnTabDetailClick(object sender, RoutedEventArgs e)
    {
        AppLog.Write($"点击详情 Tab（当前评论可见={CommentsPane.Visibility}）");
        SwitchTab(comments: false);
    }

    private void OnTabCommentsClick(object sender, RoutedEventArgs e)
    {
        AppLog.Write($"点击评论 Tab（当前评论可见={CommentsPane.Visibility}）");
        SwitchTab(comments: true);
    }

    private void SwitchTab(bool comments)
    {
        AppLog.Write($"SwitchTab({comments})");
        TabDetail.Tag = comments ? null : "active";
        TabComments.Tag = comments ? "active" : null;
        DetailPane.Visibility = comments ? Visibility.Collapsed : Visibility.Visible;
        CommentsPane.Visibility = comments ? Visibility.Visible : Visibility.Collapsed;

        // 只在第一次切到评论页时拉，之后靠滚动加载更多
        if (comments) _ = LoadCommentsAsync(reset: _commentAwemeId != CurrentId);
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
            CommentList.Items.Clear();
            CommentEmpty.Visibility = Visibility.Collapsed;
        }
        if (!_commentsHasMore) return;

        _commentsLoading = true;
        try
        {
            var page = await _client.GetCommentsAsync(id, _commentCursor);

            // 加载期间用户可能已经切走了，别把上一条的评论塞进来
            if (id != CurrentId) return;

            foreach (var c in page.Comments) CommentList.Items.Add(c);
            _commentCursor = page.Cursor;
            _commentsHasMore = page.HasMore;

            TabComments.Content = page.Total > 0 ? $"评论 {Count(page.Total)}" : "评论";

            if (CommentList.Items.Count == 0)
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
            try { await _mpv.StopAsync(); } catch { /* 已经停了 */ }
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
            foreach (int offset in new[] { 1, -1, 2, -2 })
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
        DescText.Text = string.IsNullOrWhiteSpace(item.Desc) ? "（无文案）" : item.Desc;

        var st = item.Statistics;
        LikeText.Text = $"♥ {Count(st?.DiggCount ?? 0)}";
        CommentText.Text = $"💬 {Count(st?.CommentCount ?? 0)}";
        ShareText.Text = $"↗ {Count(st?.ShareCount ?? 0)}";

        MusicText.Text = item.Music is { Title.Length: > 0 }
            ? $"♪ {item.Music.Title} — {item.Music.Author}"
            : "";

        ResetTechStats();

        // 图集用第一张图的宽高比，视频用视频的
        _aspect = item.IsImagePost && item.Images is { Count: > 0 } imgs
            ? (imgs[0].Width > 0 && imgs[0].Height > 0
                ? (double)imgs[0].Width / imgs[0].Height
                : 3.0 / 4.0)
            : item.Video?.Aspect ?? 9.0 / 16.0;

        LayoutVideo();
        UpdateIndexLabel();

        // 正在看评论的话，切作品就换成新作品的评论
        if (CommentsPane.Visibility == Visibility.Visible) _ = LoadCommentsAsync(reset: true);
    }

    private void UpdateTechStats(VideoStats s)
    {
        TechRes.Text = s.ResolutionText;
        TechFps.Text = s.FpsText;
        TechHwdec.Text = s.HwdecText;
    }

    private void ResetTechStats()
        => TechRes.Text = TechFps.Text = TechHwdec.Text = "—";

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
            switch (e.Key)
            {
                case Key.Down:
                case Key.PageDown:
                case Key.S:
                    await NextAsync();
                    e.Handled = true;
                    break;

                case Key.Up:
                case Key.PageUp:
                case Key.W:
                    await PrevAsync();
                    e.Handled = true;
                    break;

                case Key.Space:
                    if (_mpv is not null) await _mpv.TogglePauseAsync();
                    e.Handled = true;
                    break;

                case Key.Left:
                case Key.A:
                    if (_imageMode) { await StepImageAsync(-1); break; }
                    if (_mpv is not null)
                        await _mpv.SeekAsync(Math.Max(0, _mpv.Position - 5));
                    e.Handled = true;
                    break;

                case Key.Right:
                case Key.D:
                    if (_imageMode) { await StepImageAsync(1); break; }
                    if (_mpv is not null)
                        await _mpv.SeekAsync(Math.Min(_mpv.Duration, _mpv.Position + 5));
                    e.Handled = true;
                    break;

                case Key.M:
                    await ToggleMuteAsync();
                    e.Handled = true;
                    break;

                case Key.F:
                    ToggleFullscreen();
                    e.Handled = true;
                    break;

                case Key.Escape:
                    if (_fullscreen) { ToggleFullscreen(); e.Handled = true; }
                    break;

                case Key.R:
                    _authorMode = false;
                    SetStatus("换一批…");
                    await LoadFeedAsync(reset: true);
                    e.Handled = true;
                    break;

                case Key.U:
                    await LoadCurrentAuthorAsync();
                    e.Handled = true;
                    break;

                case Key.Tab:
                    SwitchTab(CommentsPane.Visibility != Visibility.Visible);
                    e.Handled = true;
                    break;

                case Key.L:
                    OnLoginClick(this, new RoutedEventArgs());
                    e.Handled = true;
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
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        var win = new LoginWindow { Owner = this };
        win.ShowDialog();

        if (string.IsNullOrEmpty(win.Cookies))
        {
            SetStatus("已取消登录");
            return;
        }

        var session = await _client.EnsureSessionAsync();
        session.LoginCookies = win.Cookies;
        session.Save();

        UpdateLoginLabel();
        SetStatus("登录成功，正在刷新推荐…");
        await LoadFeedAsync(reset: true);
    }

    private void UpdateLoginLabel()
        => LoginButton.Content = _client.Session?.IsLoggedIn == true ? "已登录" : "登录";

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        // 最大化时换成"还原"图标（两个叠起来的方框）
        MaxIcon.Data = System.Windows.Media.Geometry.Parse(maximized
            ? "M2.5,0.5 L9.5,0.5 L9.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5"
            : "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z");

        // 无边框窗口最大化时，窗口比屏幕大一圈（留给 resize 热区），
        // 不补这个边距内容边缘会被裁掉。全屏时不要边距，否则视频不铺满。
        RootGrid.Margin = maximized && !_fullscreen
            ? new Thickness(8)
            : new Thickness(0);

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
