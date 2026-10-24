using System.IO;

namespace DouyinDesktop.App;

/// <summary>
/// 最朴素的文件日志。GUI 程序出了问题看不到控制台，
/// 所以每次启动都往 `data\logs` 里写一份（跟着 exe 走；放在只读目录时才退回
/// %LOCALAPPDATA%\DouyinDesktop\logs，见 DouyinPaths）。
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string Dir = DouyinDesktop.Core.DouyinPaths.LogDirectory;

    private static string CurrentFile =>
        Path.Combine(Dir, $"app-{DateTime.Now:yyyyMMdd}.log");

    public static string LogDirectory => Dir;

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(CurrentFile, line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // 日志写不进去不能影响主流程
        }
        System.Diagnostics.Debug.WriteLine(line);
    }
}
