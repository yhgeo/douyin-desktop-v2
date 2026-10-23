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

        // --diagnose：只开诊断窗口，不启动主界面
        // --settings：只开设置窗口（调界面时用，不用先起播放器）
        // （StartupUri 已在 App.xaml 里去掉，否则会两个窗口一起开）
        // --webprobe=<awemeId>：只验证浏览器通道能不能拿到评论，然后退出
        if (e.Args.FirstOrDefault(a => a.StartsWith("--webprobe=", StringComparison.Ordinal)) is { } wp)
        {
            RunWebProbeAsync(wp["--webprobe=".Length..]);
            return;
        }

        Window win = e.Args.Contains("--diagnose")
            ? new DiagnoseWindow()
            : e.Args.Contains("--settings")
                ? new SettingsWindow(KeyMap.Load(), AppSettings.Load(),
                    DouyinDesktop.Core.Api.DouyinClient.Create(),
                    () => Task.FromResult(false), () => { })
                : e.Args.FirstOrDefault(a => a.StartsWith("--capture=", StringComparison.Ordinal)) is { } cap
                    ? new CaptureWindow(cap["--capture=".Length..])
                    : new MainWindow();

        MainWindow = win;
        win.Show();
    }

    /// <summary>自检：验证浏览器通道能否拿到评论。</summary>
    private async void RunWebProbeAsync(string awemeId)
    {
        try
        {
            var ch = new WebChannel();
            using var client = DouyinDesktop.Core.Api.DouyinClient.Create();
            client.Log += m => AppLog.Write("[webprobe] " + m);
            client.WebFetch = url => ch.FetchAsync(url);

            var page = await client.GetCommentsAsync(awemeId, 0, 20);
            AppLog.Write($"[webprobe] 评论 {page.Comments.Count} 条 / 共 {page.Total}");

            ch.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write("[webprobe] 失败：" + ex);
        }
        finally
        {
            Shutdown();
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
