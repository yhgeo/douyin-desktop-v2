using System.IO;
using System.Net.Http;
using System.Windows;
using DouyinDesktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DouyinDesktop.App;

/// <summary>
/// 调试用：用内置浏览器打开作品页，看**官方页面自己**能不能渲染出评论。
///
/// 用来区分两种可能：
///   页面有评论 → 账号/IP 没问题，是我们自己拼的请求不对
///   页面也没评论 → 账号/IP 被风控，客户端怎么写都没用
/// </summary>
public sealed class WebTestWindow : Window
{
    private readonly string _awemeId;

    public WebTestWindow(string awemeId)
    {
        _awemeId = awemeId;
        Title = "页面自检（20 秒后自动关闭）";
        Width = 1100;
        Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var web = new WebView2();
        Content = web;
        Loaded += async (_, _) => await RunAsync(web);
    }

    private async Task RunAsync(WebView2 web)
    {
        try
        {
            string dir = DouyinPaths.WebViewDirectory;
            Directory.CreateDirectory(dir);

            var env = await CoreWebView2Environment.CreateAsync(null, dir,
                new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--no-sandbox --disable-gpu" });
            await web.EnsureCoreWebView2Async(env);

            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;

            // 抓一条浏览器真实发出的评论请求，然后用同样的 Cookie 原样重放：
            //   重放成功 → 差异在签名（a_bogus / msToken）
            //   重放失败 → 差异在 Cookie / 请求头
            string? capturedUrl = null;
            core.AddWebResourceRequestedFilter("*comment/list*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => capturedUrl ??= e.Request.Uri;

            core.Navigate($"https://www.douyin.com/video/{_awemeId}");
            AppLog.Write("[自检] 已打开作品页，等 12 秒让评论渲染…");

            await Task.Delay(TimeSpan.FromSeconds(12));

            // 数一下页面里渲染出来的评论条数（评论区容器 + 各条评论的文本节点）
            const string js = @"(() => {
                const items = document.querySelectorAll('[data-e2e=""comment-item""], .comment-item');
                const texts = Array.from(items).slice(0, 3).map(e => (e.innerText || '').slice(0, 30));
                const bodyText = document.body ? document.body.innerText : '';
                const hasEmpty = bodyText.includes('暂无评论') || bodyText.includes('暂时没有更多评论');
                return JSON.stringify({ count: items.length, hasEmpty, texts });
            })()";

            string result = await core.ExecuteScriptAsync(js);
            AppLog.Write("[自检] 页面评论检查结果：" + result);

            // ===== 重放浏览器真实请求 =====
            if (capturedUrl is null)
            {
                AppLog.Write("[自检] 没抓到评论请求，跳过重放");
            }
            else
            {
                var cookies = await core.CookieManager.GetCookiesAsync("https://www.douyin.com");
                string cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var req = new HttpRequestMessage(HttpMethod.Get, capturedUrl);
                req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
                req.Headers.TryAddWithoutValidation("Referer", "https://www.douyin.com/");
                req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

                using var resp = await http.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                AppLog.Write($"[自检] 重放（浏览器 Cookie）：HTTP {(int)resp.StatusCode}，长度 {body.Length}");

                // 再用手上保存的会话 Cookie 重放同一条 URL：
                // 如果这条也成功 → Cookie 没问题，差异在签名/msToken
                var session = DouyinDesktop.Core.Api.DouyinSession.Load();
                if (session is not null)
                {
                    using var req2 = new HttpRequestMessage(HttpMethod.Get, capturedUrl);
                    req2.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
                    req2.Headers.TryAddWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
                    req2.Headers.TryAddWithoutValidation("Referer", "https://www.douyin.com/");
                    req2.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

                    using var resp2 = await http.SendAsync(req2);
                    string body2 = await resp2.Content.ReadAsStringAsync();
                    AppLog.Write($"[自检] 重放（会话 Cookie）：HTTP {(int)resp2.StatusCode}，长度 {body2.Length}");
                }
                AppLog.Write($"[自检] 抓到的 URL 长度 {capturedUrl.Length}，含 a_bogus={capturedUrl.Contains("a_bogus")}，含 msToken={capturedUrl.Contains("msToken")}，含 web-signature={capturedUrl.Contains("x-secsdk-web-signature")}");

                // 对比两边的 msToken（浏览器 vs 我们保存的）
                string browserToken = ExtractParam(capturedUrl, "msToken");
                string ourToken = DouyinDesktop.Core.Api.DouyinSession.Load()?.MsToken ?? "";
                AppLog.Write($"[自检] 浏览器 msToken 长度 {browserToken.Length}：{Head(browserToken)}");
                AppLog.Write($"[自检] 我们的 msToken 长度 {ourToken.Length}：{Head(ourToken)}　相同={browserToken == ourToken}");
            }

            Close();
        }
        catch (Exception ex)
        {
            AppLog.Write("[自检] 失败：" + ex);
            Close();
        }
    }
    private static string ExtractParam(string url, string name)
    {
        int i = url.IndexOf(name + "=", StringComparison.Ordinal);
        if (i < 0) return "";
        i += name.Length + 1;
        int end = url.IndexOf('&', i);
        return end < 0 ? url[i..] : url[i..end];
    }

    private static string Head(string s)
        => s.Length <= 24 ? s : s[..24] + "…";
}
