using System.IO;
using System.Text.Json;
using System.Windows;
using DouyinDesktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DouyinDesktop.App;

/// <summary>
/// 抓「我的喜欢 / 我的收藏」接口的真实请求。
///
/// 为什么必须走浏览器：这两个接口都要 <c>sec_user_id</c>，而自己的 sec_user_id
/// 没有任何纯 HTTP 的办法能拿到——
///   · <c>user/profile/self</c> 返回 status_code=8「用户未登录」（同一份 Cookie 下评论/主页都正常）
///   · <c>/user/self</c> 页面走纯 HTTP 只会拿到字节的 JS 反爬验证页
/// 所以只能让真浏览器（WebView2，带登录态）打开自己的主页，把页面发出的
/// <c>aweme/favorite</c> 请求抄下来：URL 里既有 sec_user_id，也有完整的参数表。
///
/// 抓到的东西存两处：
///   · <c>data/api-templates.json</c> —— 和评论模板同一套机制，之后按模板重放
///   · <c>data/my-sec-uid.txt</c>   —— 单独存一份，别的接口（收藏）也要用
///
/// 用 CDP 的 Network 域抓，而不是 <c>WebResourceRequested</c>：
/// 后者对页面 worker 发出的请求会漏（抓搜索时就是这么扑空的）。
/// </summary>
public static class FavoriteFetcher
{
    /// <summary>要抓的路径。喜欢一定有；收藏不一定（用户没收藏过就不会发请求）。</summary>
    public static readonly string[] Paths =
    {
        "/aweme/v1/web/aweme/favorite/",
        "/aweme/v1/web/aweme/listcollection/",
    };

    public static string SecUidFile => Path.Combine(DouyinPaths.DataDirectory, "my-sec-uid.txt");

    public static string? LoadSecUid()
    {
        try
        {
            return File.Exists(SecUidFile) ? File.ReadAllText(SecUidFile).Trim() : null;
        }
        catch { return null; }
    }

    public static async Task<bool> CaptureAsync(int timeoutSeconds = 25)
    {
        // WebView2 / Window 必须在 STA 线程创建
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            Task<bool> inner = await dispatcher.InvokeAsync(() => CaptureAsync(timeoutSeconds));
            return await inner;
        }

        return await CaptureCoreAsync(timeoutSeconds);
    }

    private static async Task<bool> CaptureCoreAsync(int timeoutSeconds)
    {
        Window? host = null;
        WebView2? web = null;

        try
        {
            string dir = DouyinPaths.WebViewDirectory;
            if (!Directory.Exists(dir)) return false;

            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            var seen = new List<string>();
            string secUid = "";

            host = new Window
            {
                Width = 1000,
                Height = 760,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };

            web = new WebView2();
            host.Content = web;
            host.Show();

            var env = await CoreWebView2Environment.CreateAsync(null, dir,
                new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--no-sandbox --disable-gpu" });
            await web.EnsureCoreWebView2Async(env);

            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;

            // 关键：WebView2 的 sessionid 是会话 Cookie，重启就丢，所以每次都要注入。
            // 不注入的话页面是游客态，喜欢 tab 根本不会渲染，请求也就无从抓起。
            string loginCookies = WebCookieInject.LoadLoginCookies();
            int injected = WebCookieInject.Inject(core, loginCookies);
            AppLog.Write($"[喜欢] 已注入 {injected} 条登录 Cookie（session.json）");

            // 读回验证：注入了不等于写进去了（domain / 值格式都可能被拒）
            try
            {
                var back = await core.CookieManager.GetCookiesAsync("https://www.douyin.com");
                bool hasSid = back.Any(x => x.Name == "sessionid" && x.Value.Length > 10);
                AppLog.Write($"[喜欢] Cookie 读回：共 {back.Count} 条，sessionid={(hasSid ? "在" : "不在")}");
            }
            catch (Exception ex) { AppLog.Write("[喜欢] Cookie 读回失败：" + ex.Message); }

            // ---- CDP：拿全部请求（含页面 worker 发的）----
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            var receiver = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
            receiver.DevToolsProtocolEventReceived += (_, e) =>
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
                    if (!path.StartsWith("/aweme/v1/web/", StringComparison.Ordinal)) return;

                    if (!seen.Contains(path)) seen.Add(path);

                    // sec_user_id 是这次的真正目标，任何一条带它的请求都收下
                    if (secUid.Length == 0)
                    {
                        int i = uri.IndexOf("sec_user_id=", StringComparison.Ordinal);
                        if (i > 0)
                        {
                            string rest = uri[(i + "sec_user_id=".Length)..];
                            int end = rest.IndexOf('&');
                            string val = end < 0 ? rest : rest[..end];
                            val = Uri.UnescapeDataString(val);
                            if (val.StartsWith("MS4wLjABAAAA", StringComparison.Ordinal)) secUid = val;
                        }
                    }

                    if (Array.IndexOf(Paths, path) < 0) return;
                    if (!found.ContainsKey(path)) found[path] = uri;
                }
                catch { /* 单条解析失败不影响整体 */ }
            };

            core.Navigate("https://www.douyin.com/user/self?showTab=like");
            AppLog.Write("[喜欢] 打开自己的主页（喜欢 tab），等页面发请求…");

            // 先等导航完成，再给 JS 留出渲染时间。
            // 页面是客户端渲染的，请求都是在 hydration 之后才发。
            var navDone = new TaskCompletionSource<bool>();
            void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
                => navDone.TrySetResult(e.IsSuccess);
            core.NavigationCompleted += OnNav;

            await Task.WhenAny(navDone.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            core.NavigationCompleted -= OnNav;

            await Task.Delay(TimeSpan.FromSeconds(6));

            // 页面渲染后从 DOM 里抠 sec_uid：
            // 自己的头像/主页链接一定是 /user/MS4wLjABAAAA... 的形式。
            // 这一步是纯 HTTP 拿不到的东西（纯 HTTP 只会得到 JS 反爬验证页）。
            if (secUid.Length == 0)
            {
                try
                {
                    string raw = await core.ExecuteScriptAsync(@"
(() => {
  try {
    const h = document.documentElement.innerHTML || '';
    const m = h.match(/MS4wLjABAAAA[A-Za-z0-9_\-]{20,}/);
    return JSON.stringify({
      url: location.href,
      title: document.title || '',
      secUid: m ? m[0] : '',
      len: h.length,
      text: (document.body ? (document.body.innerText || '') : '').slice(0, 160)
    });
  } catch (e) { return JSON.stringify({ err: String(e) }); }
})()");

                    // ExecuteScriptAsync 返回的是 JSON 字符串字面量，要解两层
                    string inner = raw;
                    try { inner = JsonSerializer.Deserialize<string>(raw) ?? raw; } catch { }

                    using var doc = JsonDocument.Parse(inner);
                    var root = doc.RootElement;

                    string pageUrl = root.TryGetProperty("url", out var u2) ? u2.GetString() ?? "" : "";
                    string title = root.TryGetProperty("title", out var t2) ? t2.GetString() ?? "" : "";
                    string text = root.TryGetProperty("text", out var x2) ? x2.GetString() ?? "" : "";
                    int htmlLen = root.TryGetProperty("len", out var l2) && l2.ValueKind == JsonValueKind.Number
                        ? l2.GetInt32() : 0;

                    AppLog.Write($"[喜欢] 页面状态：url={pageUrl}  title={title}  html={htmlLen} 字");
                    AppLog.Write($"[喜欢] 页面文字：{text.Replace("\n", " ")}");

                    // 页面侧再确认一次：Cookie 到底有没有发出去
                    try
                    {
                        string ck = await core.ExecuteScriptAsync(
                            "document.cookie.indexOf('sessionid')>=0 ? 'sessionid 可见' : 'sessionid 不可见(可能是 HttpOnly)'");
                        AppLog.Write("[喜欢] 页面 document.cookie：" + ck);
                    }
                    catch { }

                    if (root.TryGetProperty("secUid", out var s2) && s2.ValueKind == JsonValueKind.String)
                        secUid = s2.GetString() ?? "";

                    // 有的版本会把 /user/self 直接 replaceState 成 /user/{sec_uid}
                    if (secUid.Length == 0 && pageUrl.Contains("/user/MS4wLjABAAAA", StringComparison.Ordinal))
                    {
                        int i = pageUrl.IndexOf("MS4wLjABAAAA", StringComparison.Ordinal);
                        secUid = pageUrl[i..];
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("[喜欢] 读 DOM 失败：" + ex.Message);
                }
            }

            // 还没抓到喜欢接口就再等一会儿（收藏列表可能要靠滚动触发）
            var deadline = DateTime.Now.AddSeconds(Math.Max(6, timeoutSeconds - 12));
            while (DateTime.Now < deadline)
            {
                if (found.ContainsKey(Paths[0])) break;
                await Task.Delay(400);
            }

            if (found.Count < Paths.Length)
            {
                var extra = DateTime.Now.AddSeconds(6);
                while (DateTime.Now < extra && found.Count < Paths.Length) await Task.Delay(400);
            }

            foreach (var (path, url) in found)
            {
                ApiTemplate.Save(path, url);
                AppLog.Write($"[喜欢] 已保存 {path}（{url.Length} 字符）");
            }

            if (secUid.Length > 0)
            {
                Directory.CreateDirectory(DouyinPaths.DataDirectory);
                File.WriteAllText(SecUidFile, secUid);
                AppLog.Write($"[喜欢] sec_uid 已存：{secUid}");
            }
            else
            {
                AppLog.Write("[喜欢] 没抓到 sec_user_id（可能没登录，或页面没打开成功）");
            }

            AppLog.Write($"[喜欢] 页面发过的接口（{seen.Count} 个）：" +
                         string.Join(" | ", seen.Take(25)));

            return found.Count > 0;
        }
        catch (Exception ex)
        {
            AppLog.Write("[喜欢] 抓取失败：" + ex);
            return false;
        }
        finally
        {
            try { web?.Dispose(); } catch { }
            try { host?.Close(); } catch { }
        }
    }
}
