using System.IO;
using System.Windows;
using DouyinDesktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DouyinDesktop.App;

/// <summary>
/// 抓接口模板：后台（屏幕外）打开一次作品页，把页面真实发出的评论 / 作者作品
/// 请求 URL 抄下来存进 ApiTemplate。
///
/// 只在模板缺失或失效时调用，一次 3–5 秒；用完立刻关浏览器。
/// </summary>
public static class TemplateFetcher
{
    /// <summary>需要抓的接口路径。</summary>
    public static readonly string[] Paths =
    {
        "/aweme/v1/web/comment/list/",
        "/aweme/v1/web/aweme/post/",
    };

    public static async Task<bool> CaptureAsync(string awemeId, int timeoutSeconds = 8)
    {
        Window? host = null;
        WebView2? web = null;

        try
        {
            string dir = DouyinPaths.WebViewDirectory;
            if (!Directory.Exists(dir)) return false;

            var found = new Dictionary<string, string>();
            string pending = "";

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

            core.AddWebResourceRequestedFilter("*aweme/v1/web/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                string uri = e.Request.Uri;
                int q = uri.IndexOf('?');
                if (q < 0) return;

                string path = new Uri(uri).AbsolutePath;
                if (Array.IndexOf(Paths, path) < 0) return;

                // 同一路径只留第一条（第一条一定是页面自己发的）
                if (!found.ContainsKey(path)) found[path] = uri;
            };

            core.Navigate($"https://www.douyin.com/video/{awemeId}");
            AppLog.Write("[模板] 开始抓取，等待页面发出请求…");

            var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
            while (DateTime.Now < deadline && found.Count < Paths.Length)
                await Task.Delay(300);

            foreach (var (path, url) in found)
            {
                ApiTemplate.Save(path, url);
                AppLog.Write($"[模板] 已保存 {path}（{url.Length} 字符）");
            }

            if (found.Count == 0) AppLog.Write("[模板] 一条都没抓到");
            return found.Count > 0;
        }
        catch (Exception ex)
        {
            AppLog.Write("[模板] 抓取失败：" + ex.Message);
            return false;
        }
        finally
        {
            try { web?.Dispose(); } catch { }
            try { host?.Close(); } catch { }
        }
    }
}
