using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DouyinDesktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DouyinDesktop.App;

/// <summary>
/// 抓浏览器真实的搜索请求（URL + 全部请求头）。
///
/// 为什么需要：搜索接口 `/aweme/v1/web/search/item/` 用 x_bogus 签名、
/// 参数全补齐、Cookie 也是最新的，仍然返回 `2483 请先登录`。
/// 同一份 Cookie 下推荐流/评论/主页/详情全正常，说明搜索还有别的门槛。
/// 靠猜没用了，只能看浏览器到底带了什么。
///
/// 用法：DouyinDesktop.exe --capture-search=美女
///
/// **可见窗口 + 手动结束**：自动导航到搜索页时抖音不会自己发搜索请求
/// （实测 60 秒只发了热搜榜 hot/search/list），所以让用户在里面正常搜、
/// 正常点，看到结果后点上方「完成抓包并退出」。
/// </summary>
public static class SearchCapture
{
    private static readonly string OutPath =
        Path.Combine(DouyinPaths.DataDirectory, "search-capture.json");

    /// <summary>
    /// 是不是真正的搜索接口。
    /// 热搜榜（<c>hot/search/list</c>）路径里也含 search，必须排除——
    /// 第一版就是这么扑空的：以为抓到了，其实抓的是热搜榜。
    /// </summary>
    private static bool IsSearchPath(string path)
        => path.Contains("search/item", StringComparison.OrdinalIgnoreCase)
        || path.Contains("general/search", StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> CaptureAsync(string keyword, int timeoutSeconds = 300)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            Task<bool> inner = await dispatcher.InvokeAsync(() => CaptureAsync(keyword, timeoutSeconds));
            return await inner;
        }

        return await CaptureCoreAsync(keyword, timeoutSeconds);
    }

    private static async Task<bool> CaptureCoreAsync(string keyword, int timeoutSeconds)
    {
        string dir = DouyinPaths.WebViewDirectory;
        Directory.CreateDirectory(dir);

        var captured = new List<object>();
        var seenPaths = new List<string>();
        var capturedUrls = new HashSet<string>(StringComparer.Ordinal);
        var done = new TaskCompletionSource<bool>();
        Window? host = null;
        TextBlock? hint = null;

        // 两个抓取通道（WebResourceRequested / CDP）都会走到这里，
        // 用 URL 去重，免得同一条请求记两遍。
        bool TryAdd(string uri, string method, Dictionary<string, string> headers, string path)
        {
            if (!capturedUrls.Add(uri)) return false;
            captured.Add(new { url = uri, method, headers });
            AppLog.Write($"[抓搜索] 抓到第 {captured.Count} 条：{path}");
            return true;
        }

        void Save()
        {
            if (captured.Count == 0)
            {
                AppLog.Write($"[抓搜索] 没抓到搜索请求。页面发过的接口（{seenPaths.Count} 个）：" +
                             string.Join(" | ", seenPaths.Take(20)));
                return;
            }

            try
            {
                Directory.CreateDirectory(DouyinPaths.DataDirectory);
                File.WriteAllText(OutPath, JsonSerializer.Serialize(new
                {
                    keyword,
                    capturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    requests = captured,
                }, new JsonSerializerOptions { WriteIndented = true }));

                AppLog.Write($"[抓搜索] 抓到 {captured.Count} 条请求，已存 {OutPath}");
            }
            catch (Exception ex)
            {
                AppLog.Write("[抓搜索] 保存失败：" + ex.Message);
            }
        }

        try
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(12, 10, 12, 10),
            };

            hint = new TextBlock
            {
                Text = $"在下面搜索「{keyword}」，等结果出来、随便点开一两个视频，然后点右边按钮",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                Foreground = System.Windows.Media.Brushes.Gainsboro,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 720,
            };
            bar.Children.Add(hint);

            var finish = new Button
            {
                Content = "完成抓包并退出",
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(18, 0, 0, 0),
                FontSize = 13,
            };
            finish.Click += (_, _) => host?.Close();
            bar.Children.Add(finish);

            Grid.SetRow(bar, 0);
            grid.Children.Add(bar);

            var web = new WebView2();
            Grid.SetRow(web, 1);
            grid.Children.Add(web);

            // 手动算主屏中心：CenterScreen 在多显示器下可能落到副屏，
            // 用户会以为"没打开"。再加 Topmost，免得被主窗口盖住。
            var work = SystemParameters.WorkArea;
            const double w = 1280, h = 860;

            host = new Window
            {
                Width = w,
                Height = h,
                Left = work.Left + (work.Width - w) / 2,
                Top = work.Top + (work.Height - h) / 2,
                Title = "抖音搜索抓包 — 操作完点上方按钮",
                Background = System.Windows.Media.Brushes.Black,
                Content = grid,
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowState = WindowState.Normal,
                ShowInTaskbar = true,
            };

            host.Closed += (_, _) =>
            {
                Save();
                done.TrySetResult(captured.Count > 0);
            };

            // 抓包窗口当 MainWindow，避免某些 WPF 内部逻辑拿到 null
            if (Application.Current is not null) Application.Current.MainWindow = host;

            host.Show();
            host.Activate();

            AppLog.Write($"[抓搜索] 窗口已显示：位置 {host.Left:0},{host.Top:0} 尺寸 {host.Width:0}x{host.Height:0}，" +
                         $"工作区 {work.Width:0}x{work.Height:0}");

            var env = await CoreWebView2Environment.CreateAsync(null, dir,
                new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--no-sandbox --disable-gpu" });
            AppLog.Write("[抓搜索] WebView2 环境就绪");

            await web.EnsureCoreWebView2Async(env);
            AppLog.Write("[抓搜索] WebView2 初始化完成");

            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;

            // WebView2 的 sessionid 是会话 Cookie，进程重启就丢，浏览器里默认是游客态。
            // 游客态下搜索接口直接回 2483「请先登录」，页面自己都搜不出东西，
            // 我们自然也抓不到请求——这是之前"抓包一直不成功"的真正原因。
            int injected = WebCookieInject.Inject(core, WebCookieInject.LoadLoginCookies());
            AppLog.Write($"[抓搜索] 已注入 {injected} 条登录 Cookie");

            // 页面很重，WebView2 的渲染进程可能挂掉——记下来，别只是"窗口没了"
            core.ProcessFailed += (_, e) =>
                AppLog.Write($"[抓搜索] WebView2 进程失败：{e.ProcessFailedKind}");

            core.NavigationCompleted += (_, e) =>
                AppLog.Write($"[抓搜索] 导航完成：{(e.IsSuccess ? "成功" : "失败 " + e.WebErrorStatus)}");

            core.AddWebResourceRequestedFilter("*aweme/v1/web/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                string uri = e.Request.Uri;
                int q = uri.IndexOf('?');
                string path = q < 0 ? uri : uri[..q];
                if (!seenPaths.Contains(path)) seenPaths.Add(path);

                // 只认真正的搜索接口。热搜榜（hot/search/list）路径里也有 search，
                // 别被它骗了——第一版就是这么扑空的。
                if (!IsSearchPath(path)) return;

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in e.Request.Headers) headers[h.Key] = h.Value;

                TryAdd(uri, e.Request.Method, headers, path);
            };

            // ---- 再挂一套 CDP 抓取 ----
            // WebResourceRequested 抓不到页面 worker / service worker 发出的请求，
            // 抓搜索时就是这么漏掉的。CDP 的 Network 域能看到全部请求，两套并行更稳。
            try
            {
                await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                var netReceiver = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
                netReceiver.DevToolsProtocolEventReceived += (_, e) =>
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                        if (!doc.RootElement.TryGetProperty("request", out var req)) return;
                        if (!req.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String) return;

                        string uri = u.GetString() ?? "";
                        int q = uri.IndexOf('?');
                        if (q < 0) return;

                        string path;
                        try { path = new Uri(uri).AbsolutePath; } catch { return; }
                        if (!seenPaths.Contains(path)) seenPaths.Add(path);
                        if (!IsSearchPath(path)) return;

                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (req.TryGetProperty("headers", out var hs) && hs.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var h in hs.EnumerateObject())
                                if (h.Value.ValueKind == JsonValueKind.String)
                                    headers[h.Name] = h.Value.GetString() ?? "";
                        }

                        string method = req.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
                            ? m.GetString() ?? "GET" : "GET";

                        if (!TryAdd(uri, method, headers, path)) return;

                        // 抓到就更新界面上的提示，用户能看到"确实在抓"
                        try
                        {
                            hint.Text = $"已抓到 {captured.Count} 条搜索请求，可以点右边按钮结束了";
                        }
                        catch { }
                    }
                    catch { }
                };
            }
            catch (Exception ex)
            {
                AppLog.Write("[抓搜索] CDP 挂载失败（不影响 WebResourceRequested）：" + ex.Message);
            }

            core.Navigate($"https://www.douyin.com/search/{Uri.EscapeDataString(keyword)}?type=video");
            AppLog.Write("[抓搜索] 已导航到搜索页");

            // 用户自己关窗口；兜底超时防止忘在那儿
            await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));

            return captured.Count > 0;
        }
        catch (Exception ex)
        {
            AppLog.Write("[抓搜索] 失败：" + ex);
            return false;
        }
        finally
        {
            try { host?.Close(); } catch { /* 关窗口失败无所谓 */ }
        }
    }
}
