using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace DouyinDesktop.App;

/// <summary>
/// 扫码登录窗口。用 WebView2 打开抖音首页，让用户在页面里完成登录，
/// 然后从 WebView2 的 Cookie 仓库里把凭据整包抓出来交给 HttpClient。
///
/// 为什么不自己实现扫码协议：登录涉及验证码、风控、二次校验，是深坑。
/// 让 WebView2 跑官方页面，我们只负责"拿 Cookie"，这是成本最低且最稳的路子。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly DispatcherTimer _timer;
    private bool _done;

    /// <summary>登录成功后抓到的整包 Cookie（"a=1; b=2"）；取消或失败为 null。</summary>
    public string? Cookies { get; private set; }

    public LoginWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;

        Loaded += OnLoaded;
        Closed += (_, _) => _timer.Stop();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 独立的用户数据目录：登录状态能留住，下次不用重扫
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DouyinDesktop", "WebView2");
            Directory.CreateDirectory(dataDir);

            var options = new CoreWebView2EnvironmentOptions
            {
                // 这台机器上浏览器进程一启动就退出（exit 0x80000003）。
                // --no-sandbox 是受限环境（容器/沙箱/安全软件）里最常见的解法。
                AdditionalBrowserArguments = "--no-sandbox --disable-gpu",
            };
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
            await Web.EnsureCoreWebView2Async(env);

            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.CoreWebView2.Settings.AreDevToolsEnabled = false;

            // 浏览器进程挂了要能说清楚，否则界面只会莫名其妙停住
            Web.CoreWebView2.ProcessFailed += (_, args) =>
            {
                _timer.Stop();
                Overlay.Visibility = Visibility.Visible;
                OverlayText.Text = "浏览器进程异常退出";
                OverlayHint.Text = $"{args.ProcessFailedKind}（exit {args.ExitCode}）\n" +
                                   $"{args.FailureSourceModulePath}";
                AppLog.Write($"WebView2 ProcessFailed: {args.ProcessFailedKind} " +
                             $"exit={args.ExitCode} reason={args.Reason} " +
                             $"module={args.FailureSourceModulePath}");
            };

            Web.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    Overlay.Visibility = Visibility.Collapsed;
                }
                else
                {
                    OverlayText.Text = "登录页加载失败";
                    OverlayHint.Text = args.WebErrorStatus.ToString();
                }
            };

            OverlayText.Text = "正在打开抖音…";
            Web.CoreWebView2.Navigate("https://www.douyin.com/");

            _timer.Start();
        }
        catch (Exception ex)
        {
            OverlayText.Text = "WebView2 初始化失败";
            OverlayHint.Text = ex.Message + "\n需要安装 Edge WebView2 运行时（Win11 自带）";
            AppLog.Write("登录窗口初始化失败：" + ex);
        }
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_done) return;

        try
        {
            // 这行本身就可能抛（浏览器崩了之后访问 CoreWebView2 会 throw），
            // 所以必须放在 try 里，不能当成空判断提前 return
            var core = Web.CoreWebView2;
            if (core is null) return;

            var cookies = await core.CookieManager.GetCookiesAsync("https://www.douyin.com");

            // 登录成功的判据：拿到 sessionid。游客态不会有这个
            bool logged = cookies.Any(c =>
                (c.Name is "sessionid" or "sessionid_ss") && c.Value.Length > 10);
            if (!logged) return;

            var sb = new StringBuilder();
            foreach (var c in cookies)
            {
                if (string.IsNullOrEmpty(c.Value)) continue;
                sb.Append(c.Name).Append('=').Append(c.Value).Append("; ");
            }

            Cookies = sb.ToString().TrimEnd(' ', ';');
            _done = true;
            _timer.Stop();
            AppLog.Write($"登录成功，抓到 {cookies.Count} 个 Cookie");
            Close();
        }
        catch (InvalidOperationException ex)
        {
            // 浏览器进程崩了：停止轮询，把原因摆到界面上
            _timer.Stop();
            Overlay.Visibility = Visibility.Visible;
            OverlayText.Text = "浏览器进程异常退出";
            OverlayHint.Text = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
            AppLog.Write("WebView2 崩溃：" + ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Write("读取 Cookie 失败：" + ex.Message);
        }
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
