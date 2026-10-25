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
        var done = new TaskCompletionSource<bool>();
        Window? host = null;

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

            bar.Children.Add(new TextBlock
            {
                Text = $"在下面搜索「{keyword}」，等结果出来、随便点开一两个视频，然后点右边按钮",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                Foreground = System.Windows.Media.Brushes.Gainsboro,
            });

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

            host = new Window
            {
                Width = 1280,
                Height = 900,
                Title = "抖音搜索抓包 — 操作完点上方按钮",
                Background = System.Windows.Media.Brushes.Black,
                Content = grid,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };

            host.Closed += (_, _) =>
            {
                Save();
                done.TrySetResult(captured.Count > 0);
            };

            host.Show();

            var env = await CoreWebView2Environment.CreateAsync(null, dir,
                new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--no-sandbox --disable-gpu" });
            await web.EnsureCoreWebView2Async(env);

            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;

            core.AddWebResourceRequestedFilter("*aweme/v1/web/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                string uri = e.Request.Uri;
                int q = uri.IndexOf('?');
                string path = q < 0 ? uri : uri[..q];
                if (!seenPaths.Contains(path)) seenPaths.Add(path);

                // 只认真正的搜索接口。热搜榜（hot/search/list）路径里也有 search，
                // 别被它骗了——第一版就是这么扑空的。
                bool isRealSearch = path.Contains("search/item", StringComparison.OrdinalIgnoreCase)
                                 || path.Contains("general/search", StringComparison.OrdinalIgnoreCase);
                if (!isRealSearch) return;

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in e.Request.Headers) headers[h.Key] = h.Value;

                captured.Add(new { url = uri, method = e.Request.Method, headers });
                AppLog.Write($"[抓搜索] 抓到第 {captured.Count} 条：{path}");
            };

            core.Navigate($"https://www.douyin.com/search/{Uri.EscapeDataString(keyword)}?type=video");

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
