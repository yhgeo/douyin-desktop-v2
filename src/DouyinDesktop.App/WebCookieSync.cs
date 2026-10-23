using System.IO;
using System.Windows;
using DouyinDesktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DouyinDesktop.App;

/// <summary>
/// 从内置浏览器（持久登录的那份）里把 Cookie 读出来。
///
/// 用途：浏览器里的登录凭据会**自己续期**（只要你平时打开过抖音网页），
/// 而我们保存的那份是死的。快过期或已过期时同步一次，就能免掉重新扫码。
///
/// 只初始化内核、不加载页面，通常 1–2 秒，用完立刻销毁。
/// </summary>
public static class WebCookieSync
{
    /// <summary>读取 douyin.com 的全部 Cookie 并拼成请求头格式；失败返回 null。</summary>
    public static async Task<string?> ReadAsync()
    {
        Window? host = null;
        WebView2? web = null;

        try
        {
            string dir = DouyinPaths.WebViewDirectory;
            if (!Directory.Exists(dir)) return null;

            host = new Window
            {
                Width = 1,
                Height = 1,
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
                new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = "--no-sandbox --disable-gpu",
                });
            await web.EnsureCoreWebView2Async(env);

            var cookies = await web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.douyin.com");
            var parts = cookies
                .Where(c => !string.IsNullOrEmpty(c.Name))
                .Select(c => $"{c.Name}={c.Value}");

            string header = string.Join("; ", parts);
            return string.IsNullOrEmpty(header) ? null : header;
        }
        catch (Exception ex)
        {
            AppLog.Write("同步浏览器 Cookie 失败：" + ex.Message);
            return null;
        }
        finally
        {
            try { web?.Dispose(); } catch { }
            try { host?.Close(); } catch { }
        }
    }
}
