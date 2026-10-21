using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
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

    private MpvController? _mpv;
    private int _index = -1;
    private bool _mpvStarting;
    private bool _feedLoading;
    private bool _fullscreen;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private Rect _boundsBeforeFullscreen;
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

        // 播放器和推荐流并行准备：谁先好都不等谁
        _ = LoadFeedAsync();
        await Task.CompletedTask;
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
            var result = await _client.GetFeedAsync(10, _presented);
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

    /// <summary>缓冲区快见底时补货，避免播到第 9 条才开始等网络。</summary>
    private void EnsureBuffer()
    {
        if (_feedLoading) return;
        if (_items.Count - _index <= 3)
            _ = LoadFeedAsync();
    }

    // ================= 播放 =================

    private async Task PlayIndexAsync(int index, bool force = false)
    {
        if (index < 0 || index >= _items.Count) return;

        var item = _items[index];
        if (!item.IsPlayable && !force)
        {
            // 图集或没有 uri 的条目：跳过，避免卡住
            PushStatus($"跳过不可播条目 {item.Id}");
            if (index < _items.Count - 1) { await PlayIndexAsync(index + 1); return; }
            return;
        }

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
        PrefetchAhead(index, 2);
        UpdateIndexLabel();
        EnsureBuffer();
    }

    /// <summary>
    /// 预取后 N 条的开头一段。串行执行——下一条最要紧，别让第二条抢带宽。
    /// 每条只取 1MB，不是整条下载（实测有的视频 207MB，全下既慢又浪费）。
    ///
    /// 每次切换会取消上一轮预取：用户快速连按时，旧任务继续下只会挤占
    /// 最新那条的带宽，反而更慢。
    /// </summary>
    private void PrefetchAhead(int index, int count)
    {
        _prefetchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _prefetchCts = cts;

        _ = Task.Run(async () =>
        {
            for (int k = 1; k <= count && index + k < _items.Count; k++)
            {
                if (cts.IsCancellationRequested) return;
                var next = _items[index + k];
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

        _aspect = item.Video?.Aspect ?? 9.0 / 16.0;
        LayoutVideo();
        UpdateIndexLabel();
    }

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
                    await NextAsync();
                    e.Handled = true;
                    break;

                case Key.Up:
                case Key.PageUp:
                    await PrevAsync();
                    e.Handled = true;
                    break;

                case Key.Space:
                    if (_mpv is not null) await _mpv.TogglePauseAsync();
                    e.Handled = true;
                    break;

                case Key.Left:
                    if (_mpv is not null)
                        await _mpv.SeekAsync(Math.Max(0, _mpv.Position - 5));
                    e.Handled = true;
                    break;

                case Key.Right:
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
                    SetStatus("换一批…");
                    await LoadFeedAsync(reset: true);
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
        MaxButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";

        // 无边框窗口最大化时，窗口比屏幕大一圈（留给 resize 热区），
        // 不补这个边距内容边缘会被裁掉。全屏时不要边距，否则视频不铺满。
        RootGrid.Margin = WindowState == WindowState.Maximized && !_fullscreen
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
