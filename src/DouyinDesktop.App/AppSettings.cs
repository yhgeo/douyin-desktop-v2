using System.IO;
using System.Text.Json;

namespace DouyinDesktop.App;

/// <summary>
/// 应用设置（和快捷键分开存，避免互相覆盖）。
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// 只看图集：加载推荐流时把视频丢掉，直到凑够图集。
    ///
    /// 注意成本：推荐流里图集约 5%，凑 10 条要拉 200 条数据（约 10 次请求）。
    /// 所以实现里限制了最多拉几次，拉不到就提示，不会无限请求。
    /// </summary>
    public bool ImageOnly { get; set; }

    /// <summary>详情页是否显示快捷键提示（可以手动折叠）。</summary>
    public bool ShowShortcuts { get; set; } = true;

    /// <summary>
    /// 显示弹幕。默认关——不是每个视频都有弹幕，而且渲染走的是 mpv 的字幕轨，
    /// 关着的时候行为和不支持弹幕的版本完全一样，不会有任何副作用。
    /// </summary>
    public bool Danmaku { get; set; }

    public static string DefaultPath
        => Path.Combine(DouyinDesktop.Core.DouyinPaths.DataDirectory, "app-settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(DefaultPath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(DefaultPath))
                   ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
            File.WriteAllText(DefaultPath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // 存不下不影响本次使用
        }
    }
}
