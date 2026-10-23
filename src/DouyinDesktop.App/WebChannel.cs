using System.IO;
using System.Text.Json;
using System.Windows;
using DouyinDesktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DouyinDesktop.App;

/// <summary>
/// 浏览器通道。
///
/// 抖音对 /aweme/v1/web/* 的签名校验收紧了以后，纯 C# 拼的 a_bogus 过不了
/// （评论、详情、作者作品列表都返回空 body）。这里换个思路：
/// **不自己算签名**，用一个隐藏的 WebView2 打开抖音首页，之后在页面里 fetch——
/// 请求由抖音自己的 JS 拦截并加上签名，我们只管拿结果。
///
/// 优点：算法怎么改都不用管。
/// 代价：多一个浏览器进程（150–250MB），所以**按需创建、用完销毁**。
/// </summary>
public sealed class WebChannel : IAsyncDisposable
{
    private Window? _host;
    private WebView2? _web;
    private TaskCompletionSource<string?>? _pending;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _ready;

    /// <summary>通道是否已经起来了（用于 UI 显示状态）。</summary>
    public bool IsReady => _ready;

    private async Task<bool> EnsureReadyAsync()
    {
        if (_ready) return true;

        await _gate.WaitAsync();
        try
        {
            if (_ready) return true;

            // 窗口放到屏幕外：WebView2 必须有真实窗口才能初始化，
            // 但用户不应该看到它。
            _host = new Window
            {
                Width = 960,
                Height = 720,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                AllowsTransparency = false,
            };

            _web = new WebView2();
            _host.Content = _web;
            _host.Show();

            string dir = DouyinPaths.WebViewDirectory;
            Directory.CreateDirectory(dir);

            var env = await CoreWebView2Environment.CreateAsync(null, dir,
                new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = "--no-sandbox --disable-gpu",
                });
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = true;

            core.WebMessageReceived += (_, e) =>
            {
                string? msg;
                try { msg = e.TryGetWebMessageAsString(); }
                catch { msg = null; }
                _pending?.TrySetResult(msg);
            };

            var nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e) => nav.TrySetResult(e.IsSuccess);

            core.NavigationCompleted += OnNav;
            core.Navigate("https://www.douyin.com/");

            var done = await Task.WhenAny(nav.Task, Task.Delay(15000));
            core.NavigationCompleted -= OnNav;

            if (done != nav.Task)
            {
                AppLog.Write("浏览器通道：首页加载超时");
                return false;
            }

            _ready = true;
            AppLog.Write("浏览器通道已就绪");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("浏览器通道初始化失败：" + ex.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>在页面里 fetch 一个同源接口，返回响应体；失败返回 null。</summary>
    public async Task<string?> FetchAsync(string url, int timeoutMs = 10000)
    {
        if (!await EnsureReadyAsync()) return null;
        if (_web?.CoreWebView2 is null) return null;

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;

        string js = $@"(async () => {{
            try {{
                const r = await fetch({JsonSerializer.Serialize(url)}, {{ credentials: 'include' }});
                const t = await r.text();
                window.chrome.webview.postMessage(t);
            }} catch (e) {{
                window.chrome.webview.postMessage('__ERR__' + e);
            }}
        }})();";

        try
        {
            await _web.CoreWebView2.ExecuteScriptAsync(js);

            var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (finished != tcs.Task) return null;

            string? body = await tcs.Task;
            if (string.IsNullOrEmpty(body) || body.StartsWith("__ERR__", StringComparison.Ordinal))
                return null;

            return body;
        }
        catch (Exception ex)
        {
            AppLog.Write("浏览器通道请求失败：" + ex.Message);
            return null;
        }
        finally
        {
            _pending = null;
        }
    }

    public void Dispose()
    {
        try { _web?.Dispose(); } catch { }
        try { _host?.Close(); } catch { }
        _web = null;
        _host = null;
        _ready = false;
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.CompletedTask;
    }
}
