namespace DouyinDesktop.Core;

/// <summary>
/// 数据目录。**跟着 exe 走**——绿色版拷走整个文件夹，登录态和设置一起带走。
///
/// 如果程序放在只读位置（比如 Program Files）写不进去，
/// 自动退回 %LOCALAPPDATA%\DouyinDesktop，不至于起不来。
/// </summary>
public static class DouyinPaths
{
    public static string DataDirectory { get; } = Resolve();

    public static string SessionFile => Path.Combine(DataDirectory, "session.json");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string WebViewDirectory => Path.Combine(DataDirectory, "WebView2");
    public static string WebViewDiagnoseDirectory => Path.Combine(DataDirectory, "WebView2-Diagnose");

    private static string Resolve()
    {
        string portable = Path.Combine(AppContext.BaseDirectory, "data");

        try
        {
            Directory.CreateDirectory(portable);

            // 真的写一下，光 CreateDirectory 不能证明有写权限
            string probe = Path.Combine(portable, ".write-probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return portable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DouyinDesktop");
        }
    }
}
