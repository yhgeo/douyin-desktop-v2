using System.Windows;
using System.Windows.Threading;

namespace DouyinDesktop.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 任何没接住的异常都弹出来，而不是让窗口静默消失
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ShowFatal(ex);
        };
        base.OnStartup(e);

        // --capture-headers：只在屏幕外跑一次"抓 secsdk 头"，抓完退出。
        // 用来验证点赞所需的 x-tt-session-dtrait 能不能拿到，不启动主界面。
        if (e.Args.Contains("--capture-headers"))
        {
            RunCaptureHeaders();
            return;
        }

        // --capture-search=关键词：屏幕外打开搜索页，抓真实搜索请求（URL + 全部头）
        if (e.Args.FirstOrDefault(a => a.StartsWith("--capture-search=", StringComparison.Ordinal)) is { } cs)
        {
            RunCaptureSearch(cs["--capture-search=".Length..]);
            return;
        }

        // --diagnose：只开诊断窗口，不启动主界面
        // --settings：只开设置窗口（调界面时用，不用先起播放器）
        // （StartupUri 已在 App.xaml 里去掉，否则会两个窗口一起开）
        Window win = e.Args.Contains("--diagnose")
            ? new DiagnoseWindow()
            : e.Args.FirstOrDefault(a => a.StartsWith("--webtest=", StringComparison.Ordinal)) is { } wt
                ? new WebTestWindow(wt["--webtest=".Length..])
            : e.Args.Contains("--settings")
                ? new SettingsWindow(KeyMap.Load(), AppSettings.Load(),
                    DouyinDesktop.Core.Api.DouyinClient.Create(),
                    () => Task.FromResult(false), () => { })
                : new MainWindow();

        MainWindow = win;
        win.Show();
    }

    /// <summary>屏幕外打开一次搜索页，抓真实搜索请求。</summary>
    private async void RunCaptureSearch(string keyword)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(keyword)) keyword = "美女";

            AppLog.Write($"[抓搜索] 开始，关键词 {keyword}");
            bool ok = await SearchCapture.CaptureAsync(keyword);
            AppLog.Write(ok ? "[抓搜索] 完成" : "[抓搜索] 失败");
            Shutdown(ok ? 0 : 1);
        }
        catch (Exception ex)
        {
            AppLog.Write("[抓搜索] 异常：" + ex);
            Shutdown(1);
        }
    }

    /// <summary>
    /// 用最近一次现场里的作品 id，在屏幕外打开一次作品页，把 secsdk 头抄下来。
    /// 窗口位置在 -32000，不进任务栏，用户看不到。
    /// </summary>
    private async void RunCaptureHeaders()
    {
        try
        {
            var snap = DouyinDesktop.Core.LastSession.Load();
            string id = snap?.Items.FirstOrDefault(x => x.IsPlayable)?.Id ?? "";

            if (id.Length == 0)
            {
                AppLog.Write("[抓头] 没有可用的作品 id（先正常用一次程序，或者直接开一次评论）");
                Shutdown(1);
                return;
            }

            AppLog.Write($"[抓头] 用作品 {id} 抓 secsdk 头");
            bool ok = await TemplateFetcher.CaptureAsync(id, 15);
            AppLog.Write(ok ? "[抓头] 完成" : "[抓头] 失败");
            Shutdown(ok ? 0 : 1);
        }
        catch (Exception ex)
        {
            AppLog.Write("[抓头] 异常：" + ex);
            Shutdown(1);
        }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatal(e.Exception);
        e.Handled = true;
    }

    private static void ShowFatal(Exception ex)
    {
        AppLog.Write("未处理异常：" + ex);
        MessageBox.Show(ex.ToString(), "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
