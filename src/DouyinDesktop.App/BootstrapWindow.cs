using System.IO;
using System.Text.Json;
using System.Windows;
using DouyinDesktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DouyinDesktop.App;

/// <summary>
/// 一次性"密钥初始化"：用已登录的 WebView2 打开抖音页面，
/// 让抖音自己的 SDK 去申请 bd-ticket-guard 的密钥，然后把 cookie 全部倒出来，
/// 重点看有没有 `bd_ticket_guard_server_data`（评论/点赞要的那把钥匙）。
///
/// 拿到之后就不需要浏览器了，后续用纯 C# 算签名头。
/// </summary>
public sealed class BootstrapWindow : Window
{
    private readonly string _awemeId;

    public BootstrapWindow(string awemeId)
    {
        _awemeId = awemeId;
        Title = "初始化密钥（约 20 秒后自动关闭）";
        Width = 1000;
        Height = 760;
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

            // 打开作品页：播放/评论相关的 SDK 会在这里初始化并申请密钥
            core.Navigate($"https://www.douyin.com/video/{_awemeId}");
            AppLog.Write("[bootstrap] 已打开作品页，等待 SDK 初始化…");

            await Task.Delay(TimeSpan.FromSeconds(20));

            var cookies = await core.CookieManager.GetCookiesAsync("https://www.douyin.com");
            var names = cookies.Select(c => c.Name).OrderBy(n => n).ToList();
            AppLog.Write($"[bootstrap] 共 {cookies.Count} 个 cookie：{string.Join(",", names)}");

            var target = cookies.FirstOrDefault(c => c.Name == "bd_ticket_guard_server_data");
            if (target is null)
            {
                AppLog.Write("[bootstrap] 没有 bd_ticket_guard_server_data —— 这条路走不通");
            }
            else
            {
                AppLog.Write($"[bootstrap] 拿到 bd_ticket_guard_server_data，长度 {target.Value.Length}");
                AppLog.Write("[bootstrap] 内容：" + target.Value[..Math.Min(200, target.Value.Length)]);
            }

            var clientData = cookies.FirstOrDefault(c => c.Name == "bd_ticket_guard_client_data");
            if (clientData is not null)
                AppLog.Write($"[bootstrap] client_data 长度 {clientData.Value.Length}");

            Close();
        }
        catch (Exception ex)
        {
            AppLog.Write("[bootstrap] 失败：" + ex);
            Close();
        }
    }
}
