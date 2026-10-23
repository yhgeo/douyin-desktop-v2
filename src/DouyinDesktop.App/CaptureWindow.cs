using System.IO;
using System.Windows;
using DouyinDesktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DouyinDesktop.App;

/// <summary>
/// 调试用抓包窗口：用**已登录的 WebView2 用户目录**打开作品页，
/// 把网页版真实的评论请求（URL + 关键请求头）写进日志，
/// 用来和我们自己拼的请求逐字段对比。
///
/// 只用来定位签名/请求头差异，不参与正常播放。
/// </summary>
public sealed class CaptureWindow : Window
{
    private readonly string _url;

    public CaptureWindow(string awemeId)
    {
        _url = $"https://www.douyin.com/video/{awemeId}";
        Title = "抓包（20 秒后自动关闭）";
        Width = 1000;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var web = new WebView2();
        Content = web;
        Loaded += async (_, _) => await InitAsync(web);
    }

    private async Task InitAsync(WebView2 web)
    {
        try
        {
            string dataDir = DouyinPaths.WebViewDirectory;
            Directory.CreateDirectory(dataDir);

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--no-sandbox --disable-gpu",
            };
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
            await web.EnsureCoreWebView2Async(env);

            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;

            core.AddWebResourceRequestedFilter("*comment/list*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                try
                {
                    var h = e.Request.Headers;
                    string Get(string k) => h.Contains(k) ? h.GetHeader(k) : "(无)";
                    string cookie = Get("Cookie");

                    AppLog.Write($"[抓包] {e.Request.Method} {e.Request.Uri}");
                    AppLog.Write($"[抓包] Referer={Get("Referer")}");
                    AppLog.Write($"[抓包] csrf={Get("x-secsdk-csrf-token")}");
                    AppLog.Write($"[抓包] UA={Get("User-Agent")}");
                    AppLog.Write($"[抓包] cookie={cookie[..Math.Min(160, cookie.Length)]}");
                }
                catch (Exception ex)
                {
                    AppLog.Write("[抓包] 记录失败：" + ex.Message);
                }
            };

            core.Navigate(_url);
            AppLog.Write("[抓包] 已打开 " + _url);

            await Task.Delay(TimeSpan.FromSeconds(20));
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Write("[抓包] 初始化失败：" + ex.Message);
            Close();
        }
    }
}
