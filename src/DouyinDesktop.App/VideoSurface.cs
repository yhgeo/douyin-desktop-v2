using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DouyinDesktop.App;

/// <summary>
/// 给 mpv 用的「画布」：一个 Win32 子窗口，尺寸由 WPF 布局决定。
///
/// 为什么必须走 HwndHost：mpv 是独立进程，它只认 HWND。WPF 的视觉树是 DirectX 画的，
/// 没有 HWND 给它——所以要挖一个真正的子窗口出来，把句柄交给 mpv 的 --wid。
///
/// 代价是 airspace：这个子窗口永远盖在 WPF 内容之上，任何 WPF 元素都盖不住它。
/// 所以布局上不能让文字/按钮和视频区重叠——信息栏放在视频区右侧。
/// </summary>
public sealed class VideoSurface : HwndHost
{
    private IntPtr _hwnd;
    private WndProcDelegate? _wndProc;   // 必须保住引用，否则被 GC 回收后回调就崩了
    private IntPtr _oldWndProc;

    public IntPtr VideoHandle => _hwnd;
    public bool IsHandleCreated => _hwnd != IntPtr.Zero;

    /// <summary>子窗口创建完成。mpv 只能在这之后启动。</summary>
    public event EventHandler<IntPtr>? HandleCreated;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = Native.CreateWindowEx(
            0, "static", string.Empty,
            Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPCHILDREN | Native.WS_CLIPSIBLINGS,
            0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight),
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        SubclassForBlackBackground();
        FillBlack();

        HandleCreated?.Invoke(this, _hwnd);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_wndProc is not null)
            Native.SetWindowLongPtr(hwnd.Handle, Native.GWLP_WNDPROC, _oldWndProc);
        Native.DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    /// <summary>尺寸变了就重新铺黑底，避免拖动窗口时闪出 static 控件的默认灰。</summary>
    protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        FillBlack();
    }

    /// <summary>
    /// 子类化窗口过程，把 WM_ERASEBKGND 拦下来自己涂黑。
    /// 不用 SetClassLongPtr 改背景刷——static 是全局共享的窗口类，改了会影响别的控件。
    /// </summary>
    private void SubclassForBlackBackground()
    {
        _wndProc = (hWnd, msg, wParam, lParam) =>
        {
            switch (msg)
            {
                case Native.WM_ERASEBKGND:
                    FillBlack(hWnd);
                    return new IntPtr(1);
                case Native.WM_PAINT:
                    FillBlack(hWnd);
                    break;
            }
            return Native.CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        };

        _oldWndProc = Native.SetWindowLongPtr(_hwnd, Native.GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProc));
    }

    private void FillBlack() => FillBlack(_hwnd);

    private static void FillBlack(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!Native.GetClientRect(hwnd, out var rc)) return;

        IntPtr dc = Native.GetDC(hwnd);
        if (dc == IntPtr.Zero) return;
        try
        {
            IntPtr brush = Native.CreateSolidBrush(0x000000);
            try { Native.FillRect(dc, ref rc, brush); }
            finally { Native.DeleteObject(brush); }
        }
        finally { Native.ReleaseDC(hwnd, dc); }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static class Native
    {
        public const int WS_CHILD = 0x40000000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CLIPCHILDREN = 0x02000000;
        public const int WS_CLIPSIBLINGS = 0x04000000;

        public const uint WM_PAINT = 0x000F;
        public const uint WM_ERASEBKGND = 0x0014;

        public const int GWLP_WNDPROC = -4;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        public static extern IntPtr CallWindowProc(IntPtr prevWndProc, IntPtr hwnd, uint msg,
            IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateSolidBrush(int color);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll")]
        public static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);
    }
}
