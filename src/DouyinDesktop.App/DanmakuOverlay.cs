using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DouyinDesktop.Core.Models;

namespace DouyinDesktop.App;

/// <summary>
/// 弹幕浮层：一个跟着视频区域走的透明窗口，自己按**显示器刷新率**画弹幕。
///
/// 为什么要另开一个窗口：
///   视频是 HwndHost（独立 Win32 子窗口），按 airspace 规则它永远盖在主窗口的
///   WPF 内容之上，弹幕画在主窗口里根本看不见。
///   而走 ASS 字幕轨又受视频帧率限制（30fps 的视频，弹幕一帧要跳 6-7px，
///   人眼追踪时会整合成拖影——这就是"移动时发糊"）。
///
/// 这个窗口：
///   · Owner = 主窗口 → 天然叠在它上面，最小化时跟着隐藏
///   · 透明 + WS_EX_TRANSPARENT → 不挡鼠标，点视频照常暂停
///   · 60Hz 定时器 + 插值时钟 → 位移只有 30fps 的一半以下
///   · 文字用 Geometry 描边绘制，和 ASS 那套的观感一致
/// </summary>
public sealed class DanmakuOverlay : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x80;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly Window _owner;
    private readonly FrameworkElement _target;
    private readonly Func<double> _position;
    private readonly Func<bool> _paused;
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly DispatcherTimer _timer;

    private List<DanmakuItem> _items = new();
    private int _next;
    private readonly List<Live> _live = new();
    private long[] _laneFreeAt = Array.Empty<long>();
    private DanmakuStyle _style = DanmakuStyle.Default;
    private Typeface _typeface = new("Microsoft YaHei");

    // 插值时钟：mpv 的 time-pos 是跳着来的（几 Hz），直接用会一顿一顿的
    private double _clockMs;
    private long _lastTickTicks;

    private sealed class Live
    {
        public required DanmakuVisual Visual { get; init; }
        public required TranslateTransform Move { get; init; }
        public required double TextWidth { get; init; }
        public double StartMs { get; init; }
    }

    public DanmakuOverlay(Window owner, FrameworkElement target,
                          Func<double> position, Func<bool> paused)
    {
        _owner = owner;
        _target = target;
        _position = position;
        _paused = paused;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Content = _canvas;

        SourceInitialized += (_, _) => MakeClickThrough();
        owner.LocationChanged += (_, _) => SyncBounds();
        owner.SizeChanged += (_, _) => SyncBounds();
        target.SizeChanged += (_, _) => SyncBounds();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void MakeClickThrough()
    {
        var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    // ================= 对外 =================

    public void SetStyle(DanmakuStyle style)
    {
        _style = style;
        Reload();
        SyncBounds();       // 区域/字号变了，带子的位置和高度也会变
    }

    /// <summary>换一批弹幕（同一视频内追加也算一次重载）。</summary>
    public void SetItems(IEnumerable<DanmakuItem> items)
    {
        _items = items.OrderBy(x => x.OffsetMs).ToList();
        Reload();
        if (!IsVisible) Show();
    }

    public void Clear()
    {
        _items = new List<DanmakuItem>();
        Reload();
        if (IsVisible) Hide();
    }

    private void Reload()
    {
        foreach (var d in _live) _canvas.Children.Remove(d.Visual);
        _live.Clear();

        double nowMs = _position() * 1000;
        _clockMs = nowMs;
        _lastTickTicks = 0;

        // 跳过已经播过去的那部分，否则一重载就会把前面几十条一次性喷出来
        _next = 0;
        while (_next < _items.Count && _items[_next].OffsetMs < nowMs - 500) _next++;

        _laneFreeAt = new long[LaneCount()];
    }

    // ================= 每帧 =================

    private void OnTick(object? sender, EventArgs e)
    {
        // 浮层出问题绝对不能把整个程序带崩：出错就先忍着，错太多次就自我停用
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            if (++_failures < 20) return;

            _timer.Stop();
            if (IsVisible) Hide();
            AppLog.Write("弹幕浮层已停用：" + ex);
        }
    }

    private int _failures;

    private void Tick()
    {
        if (_target.ActualWidth < 8 || _target.ActualHeight < 8) return;

        SyncBounds();
        double now = NowMs();

        SpawnDue(now);

        double w = _target.ActualWidth;
        double travel = Math.Clamp(_style.SpeedSeconds, 4, 20) * 1000;

        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var d = _live[i];
            double p = (now - d.StartMs) / travel;
            if (p >= 1)
            {
                _canvas.Children.Remove(d.Visual);
                _live.RemoveAt(i);
                continue;
            }
            d.Move.X = w - (w + d.TextWidth) * p;
        }
    }

    private double NowMs()
    {
        long t = Stopwatch.GetTimestamp();
        double dt = _lastTickTicks == 0 ? 0 : (t - _lastTickTicks) * 1000.0 / Stopwatch.Frequency;
        _lastTickTicks = t;

        double pos = _position() * 1000;

        if (_paused()) { _clockMs = pos; return pos; }   // 暂停就钉住

        _clockMs += dt;
        // 拖动进度条 / 换视频：偏差大了直接对齐，不要慢慢追
        if (Math.Abs(_clockMs - pos) > 300) _clockMs = pos;
        return _clockMs;
    }

    private void SpawnDue(double now)
    {
        double w = _target.ActualWidth;

        while (_next < _items.Count && _items[_next].OffsetMs <= now)
        {
            var item = _items[_next++];

            // 已经过时太久的不要了，硬塞进来会突然出现在屏幕中间
            if (now - item.OffsetMs > 1500) continue;

            string text = Clean(item.Text);
            if (text.Length == 0) continue;

            var (geo, width) = BuildText(text);
            if (geo is null || width <= 0) continue;

            int lane = PickLane(now);
            if (lane < 0) continue;    // 轨道全满，这条丢掉

            var move = new TranslateTransform(w, 0);
            var visual = new DanmakuVisual(geo, Brushes.White, OutlinePen())
            {
                RenderTransform = move,
            };
            Canvas.SetLeft(visual, 0);
            Canvas.SetTop(visual, LaneTop(lane));
            _canvas.Children.Add(visual);

            double travel = Math.Clamp(_style.SpeedSeconds, 4, 20) * 1000;
            // 同轨道：等上一条的右边缘离开屏幕右侧 gap 像素，才放下一条
            double gap = Math.Max(24, _target.ActualWidth * 0.03);
            _laneFreeAt[lane] = (long)(now + travel * (width + gap) / (w + width));

            _live.Add(new Live { Visual = visual, Move = move, TextWidth = width, StartMs = now });
        }
    }

    // ================= 布局 =================

    private double DanmakuFontSize => Math.Max(10, _target.ActualHeight * Math.Clamp(_style.FontPercent, 1.5, 8.0) / 100.0);

    private double LaneHeight => DanmakuFontSize * 1.7;

    /// <summary>区域占视频高度的比例区间。</summary>
    private (double Top, double Bottom) Area()
    {
        return _style.Area switch
        {
            DanmakuArea.Top => (0.02, 0.20),
            DanmakuArea.Middle => (0.40, 0.62),
            DanmakuArea.Bottom => (0.78, 0.97),
            _ => (0.02, 0.97),
        };
    }

    /// <summary>
    /// 浮层窗口只盖住弹幕那条带，不是整个视频区。
    /// 透明窗口每帧要合成整块位图，覆盖整个视频区太浪费。
    /// </summary>
    private (double Top, double Height) Band()
    {
        var (t, b) = Area();
        double h = _target.ActualHeight;
        return (h * t, Math.Max(LaneHeight, h * (b - t)));
    }

    private int LaneCount()
    {
        var (_, height) = Band();
        return Math.Clamp((int)(height / LaneHeight), 1, 10);
    }

    private double LaneTop(int lane) => lane * LaneHeight;

    private int PickLane(double now)
    {
        int best = -1;
        long bestFree = long.MaxValue;
        for (int i = 0; i < _laneFreeAt.Length; i++)
        {
            if (_laneFreeAt[i] < bestFree) { bestFree = _laneFreeAt[i]; best = i; }
        }
        // 最空的那条也要等 0.6 秒以上，说明确实排满了
        return bestFree - now > 600 ? -1 : best;
    }

    private (Geometry? Geo, double Width) BuildText(string text)
    {
        try
        {
            double size = DanmakuFontSize;
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                _typeface, size, Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            var geo = ft.BuildGeometry(new Point(0, 0));
            return (geo, ft.WidthIncludingTrailingWhitespace);
        }
        catch (ArgumentException)
        {
            return (null, 0);
        }
    }

    private Pen OutlinePen()
    {
        double size = DanmakuFontSize;
        double width = size * Math.Clamp(_style.OutlineRatio, 0, 0.3);
        if (width <= 0.2) return new Pen(Brushes.Transparent, 0);

        return new Pen(Brushes.Black, width)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
    }

    private void SyncBounds()
    {
        // 主窗口最小化、或者不是活动窗口时（比如打开了设置对话框、切到别的程序）
        // 就藏起来。不藏的话全屏时浮层是 topmost，会盖在设置窗口上面。
        if (_owner.WindowState == WindowState.Minimized || !_owner.IsActive)
        {
            if (IsVisible) Hide();
            return;
        }
        if (!IsVisible && _items.Count > 0) Show();

        // 主窗口全屏时会设 Topmost=true，浮层不同步就会被压到它下面 —— 表现就是"全屏后弹幕消失"
        if (Topmost != _owner.Topmost) Topmost = _owner.Topmost;

        if (_target.ActualHeight < 8) return;

        var (bandTop, bandHeight) = Band();

        // 用相对主窗口的偏移换算，避开 DPI 换算的坑
        Point rel = _target.TranslatePoint(new Point(0, bandTop), _owner);
        double left = _owner.Left + rel.X;
        double top = _owner.Top + rel.Y;

        if (Math.Abs(Left - left) > 0.5) Left = left;
        if (Math.Abs(Top - top) > 0.5) Top = top;
        if (Math.Abs(Width - _target.ActualWidth) > 0.5) Width = _target.ActualWidth;
        if (Math.Abs(Height - bandHeight) > 0.5) Height = bandHeight;
    }

    private static string Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return raw.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    /// <summary>只负责把缓存的 Geometry 画出来，位置由 RenderTransform 挪。</summary>
    private sealed class DanmakuVisual(Geometry geo, Brush fill, Pen pen) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) => dc.DrawGeometry(fill, pen, geo);
    }
}
