using System.IO;
using System.Text.Json;
using System.Windows;
using DouyinDesktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DouyinDesktop.App;

/// <summary>
/// 抓浏览器真实的搜索请求（URL + 全部请求头）。
///
/// 为什么需要它：搜索接口 `/aweme/v1/web/search/item/` 用 x_bogus 签名、
/// 参数全补齐、Cookie 也是最新的，仍然返回 `2483 请先登录`。
/// 同一份 Cookie 下推荐流/评论/主页/详情全正常，说明搜索还有别的门槛。
/// 靠猜没用了，只能看浏览器到底带了什么。
///
/// 用法：DouyinDesktop.exe --capture-search=美女
/// 窗口开在屏幕外，用户看不到；抓到就退出。
/// </summary>
public static class SearchCapture
{
    private static readonly string OutPath =
        Path.Combine(DouyinPaths.DataDirectory, "search-capture.json");

    public static async Task<bool> CaptureAsync(string keyword, int timeoutSeconds = 60)
    {
        // WebView2 + Window 只能在 UI（STA）线程创建
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
        Window? host = null;

        try
        {
            host = new Window
            {
                Width = 1200,
                Height = 900,
                Left = -32000,
                Top = -32000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
            };

            var web = new WebView2();
            host.Content = web;
            host.Show();

            var env = await CoreWebView2Environment.CreateAsync(null, dir,
                new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--no-sandbox --disable-gpu" });
            await web.EnsureCoreWebView2Async(env);

            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;

            // 先抓**所有** /aweme/v1/web/ 请求：搜索页可能先打别的接口，
            // 也可能用 general/search/single 而不是 search/item。
            // 只盯着 search 一条容易一无所获（第一次就是这么扑空的）。
            var seenPaths = new List<string>();

            core.AddWebResourceRequestedFilter("*aweme/v1/web/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                string uri = e.Request.Uri;

                // 记录路径（去掉 query），方便看页面到底发了哪些接口
                int q = uri.IndexOf('?');
                string path = q < 0 ? uri : uri[..q];
                if (!seenPaths.Contains(path)) seenPaths.Add(path);

                // 只认**真正的搜索接口**。热搜榜（hot/search/list）路径里也有 search，
                // 第一版就是被它骗了、抓到一条就提前退出，结果永远等不到搜索本身。
                bool isRealSearch = path.Contains("search/item", StringComparison.OrdinalIgnoreCase)
                                 || path.Contains("general/search", StringComparison.OrdinalIgnoreCase);
                if (!isRealSearch) return;

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in e.Request.Headers) headers[h.Key] = h.Value;

                captured.Add(new
                {
                    url = uri,
                    method = e.Request.Method,
                    headers,
                });
            };

            core.NavigationCompleted += (_, e) =>
                AppLog.Write($"[抓搜索] 导航完成：{(e.IsSuccess ? "成功" : "失败 " + e.WebErrorStatus)}");

            // 带 type=video：直接落到"视频"标签页，比首页更容易触发真正的搜索请求
            core.Navigate($"https://www.douyin.com/search/{Uri.EscapeDataString(keyword)}?type=video");

            // 等第一条搜索请求；抓到就提前结束，不用干等
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline && captured.Count == 0)
                await Task.Delay(300);

            if (captured.Count == 0)
            {
                AppLog.Write($"[抓搜索] {timeoutSeconds}s 内没抓到搜索请求。" +
                             $"页面发过的接口（{seenPaths.Count} 个）：" +
                             string.Join(" | ", seenPaths.Take(15)));
                return false;
            }

            Directory.CreateDirectory(DouyinPaths.DataDirectory);
            File.WriteAllText(OutPath, JsonSerializer.Serialize(new
            {
                keyword,
                capturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                requests = captured,
            }, new JsonSerializerOptions { WriteIndented = true }));

            AppLog.Write($"[抓搜索] 抓到 {captured.Count} 条请求，已存 {OutPath}");
            return true;
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
