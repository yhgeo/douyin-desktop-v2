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
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatal(e.Exception);
        e.Handled = true;
    }

    private static void ShowFatal(Exception ex)
    {
        MessageBox.Show(ex.ToString(), "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
