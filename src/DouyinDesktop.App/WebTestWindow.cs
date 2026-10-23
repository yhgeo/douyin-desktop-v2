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
                AppLog.Write($"[自检] 浏览器 msToken 长度 {browserToken.Length}：{browserToken}");
                AppLog.Write($"[自检] 我们的 msToken 长度 {ourToken.Length}：{ourToken}　相同={browserToken == ourToken}");

                // ===== 关键对比：同一条 query，浏览器算的 a_bogus vs 我们算的 =====
                string browserAbogus = Uri.UnescapeDataString(ExtractParam(capturedUrl, "a_bogus"));
                string queryWithSign = new Uri(capturedUrl).Query.TrimStart('?');
                int cut = queryWithSign.IndexOf("&a_bogus=", StringComparison.Ordinal);
                string baseQuery = cut > 0 ? queryWithSign[..cut] : queryWithSign;

                var signer = new DouyinDesktop.Core.Signing.ABogus(false,
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
                string ourAbogus = signer.SignQuery(baseQuery, "", "www.douyin.com");

                AppLog.Write($"[自检] a_bogus 长度：浏览器 {browserAbogus.Length}，我们 {ourAbogus.Length}");
                AppLog.Write($"[自检] 浏览器 a_bogus：{browserAbogus}");
                AppLog.Write($"[自检] 我们的 a_bogus：{ourAbogus}");

                int diffAt = -1;
                int minLen = Math.Min(browserAbogus.Length, ourAbogus.Length);
                for (int i = 0; i < minLen; i++)
                {
                    if (browserAbogus[i] != ourAbogus[i]) { diffAt = i; break; }
                }
                AppLog.Write($"[自检] 首个不同位置：{diffAt}（长度相同时 -1 表示完全一致）");

                // 顺便把这条 URL 存成模板，供客户端直接使用
                ApiTemplate.Save("/aweme/v1/web/comment/list/", capturedUrl);
                AppLog.Write("[自检] 已保存评论接口模板");

                // 把浏览器真实使用的 webid 记下来（强校验接口要核对它）
                string browserWebId = ExtractParam(capturedUrl, "webid");
                if (browserWebId.Length > 10)
                {
                    string wpath = Path.Combine(DouyinPaths.DataDirectory, "webid.txt");
                    Directory.CreateDirectory(DouyinPaths.DataDirectory);
                    File.WriteAllText(wpath, browserWebId);
                    AppLog.Write($"[自检] 已把浏览器 webid 写入 {wpath}：{browserWebId}");
                }

                // ===== 终极测试：浏览器 URL，只把 a_bogus 换成我们算的 =====
                string oursUrl = "https://www.douyin.com/aweme/v1/web/comment/list/?" +
                                 baseQuery + "&a_bogus=" + Uri.EscapeDataString(ourAbogus);

                using var http2 = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var reqOurs = new HttpRequestMessage(HttpMethod.Get, oursUrl);
                reqOurs.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                reqOurs.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
                reqOurs.Headers.TryAddWithoutValidation("Referer", "https://www.douyin.com/");
                reqOurs.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

                using var respOurs = await http2.SendAsync(reqOurs);
                string bodyOurs = await respOurs.Content.ReadAsStringAsync();
                AppLog.Write($"[自检] 换成我们的 a_bogus：HTTP {(int)respOurs.StatusCode}，长度 {bodyOurs.Length}");

                // ===== 逐个替换参数值，定位是哪个值不被接受 =====
                var session3 = DouyinDesktop.Core.Api.DouyinSession.Load();
                string ourWebId = "";
                try
                {
                    string p = Path.Combine(DouyinPaths.DataDirectory, "webid.txt");
                    if (File.Exists(p)) ourWebId = File.ReadAllText(p).Trim();
                }
                catch { }

                var probes = new (string Name, string Value)[]
                {
                    ("webid", ourWebId),
                    ("uifid", session3?.EffectiveUifid ?? ""),
                    ("msToken", session3?.MsToken ?? ""),
                };

                foreach (var (name, value) in probes)
                {
                    if (string.IsNullOrEmpty(value)) { AppLog.Write($"[自检] 替换 {name}：没有值，跳过"); continue; }

                    string swapped = ReplaceParam(capturedUrl, name, value);
                    string reSigned = ReSign(swapped);
                    if (reSigned is null) { AppLog.Write($"[自检] 替换 {name}：重签失败"); continue; }

                    using var rq = new HttpRequestMessage(HttpMethod.Get, reSigned);
                    rq.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                    rq.Headers.TryAddWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
                    rq.Headers.TryAddWithoutValidation("Referer", "https://www.douyin.com/");
                    rq.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

                    using var rs = await http2.SendAsync(rq);
                    string b = await rs.Content.ReadAsStringAsync();
                    AppLog.Write($"[自检] 替换 {name}（长度 {value.Length}）：HTTP {(int)rs.StatusCode}，长度 {b.Length}");
                }
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

    /// <summary>把 URL 里某个参数的值换成新值（保留其余原样）。</summary>
    private static string ReplaceParam(string url, string name, string value)
    {
        string marker = name + "=";
        int i = url.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return url;

        int start = i + marker.Length;
        int end = url.IndexOf('&', start);
        if (end < 0) end = url.Length;

        return url[..start] + Uri.EscapeDataString(value) + url[end..];
    }

    /// <summary>把 URL 里旧的 a_bogus 去掉，按新 query 重算一个再拼上。</summary>
    private static string? ReSign(string url)
    {
        int cut = url.IndexOf("&a_bogus=", StringComparison.Ordinal);
        if (cut < 0) return null;

        string head = url[..cut];
        int q = head.IndexOf('?');
        if (q < 0) return null;

        string query = head[(q + 1)..];
        var signer = new DouyinDesktop.Core.Signing.ABogus(false,
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
        string sign = signer.SignQuery(query, "", "www.douyin.com");
        return head + "&a_bogus=" + Uri.EscapeDataString(sign);
    }
}
