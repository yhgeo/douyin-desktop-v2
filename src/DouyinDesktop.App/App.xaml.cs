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
        // （StartupUri 已在 App.xaml 里去掉，否则会两个窗口一起开）
        Window win = e.Args.Contains("--diagnose")
            ? new DiagnoseWindow()
            : new MainWindow();

        MainWindow = win;
        win.Show();
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
