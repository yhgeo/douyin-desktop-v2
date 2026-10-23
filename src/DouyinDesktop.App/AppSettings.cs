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

    // ---- 弹幕外观（都是相对量，换分辨率不用重调）----

    /// <summary>字号占画面高度的百分比。</summary>
    public double DanmakuFontPercent { get; set; } = 3.2;

    /// <summary>不透明度 0-100。</summary>
    public int DanmakuOpacity { get; set; } = 90;

    /// <summary>描边宽度，占字号的百分比（0 = 不描边）。</summary>
    public double DanmakuOutlinePercent { get; set; } = 10;

    /// <summary>一条弹幕从进入到完全离开用几秒（4 - 20）。</summary>
    public double DanmakuSpeed { get; set; } = 10;

    /// <summary>显示区域：top / middle / bottom / full。</summary>
    public string DanmakuArea { get; set; } = "top";

    /// <summary>
    /// 渲染方式：
    ///   ass     = 交给 mpv 渲染成字幕轨（默认，稳定；受视频帧率限制，快速移动时有轻微跳动）
    ///   overlay = 独立透明浮层，按显示器刷新率画（更顺滑，但窗口层级相关 bug 多）
    /// </summary>
    public string DanmakuRenderer { get; set; } = "ass";

    public DouyinDesktop.Core.Models.DanmakuStyle ToDanmakuStyle() => new()
    {
        FontPercent = DanmakuFontPercent,
        Opacity = DanmakuOpacity,
        OutlineRatio = DanmakuOutlinePercent / 100.0,
        SpeedSeconds = DanmakuSpeed,
        Area = DanmakuArea switch
        {
            "middle" => DouyinDesktop.Core.Models.DanmakuArea.Middle,
            "bottom" => DouyinDesktop.Core.Models.DanmakuArea.Bottom,
            "full" => DouyinDesktop.Core.Models.DanmakuArea.Full,
            _ => DouyinDesktop.Core.Models.DanmakuArea.Top,
        },
    };

    public static string DefaultPath
        => Path.Combine(DouyinDesktop.Core.DouyinPaths.DataDirectory, "app-settings.json");

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(DefaultPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(DefaultPath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            s = new AppSettings();
        }

        // 写回一次：弹幕外观这些项没有界面控件，只在配置文件里调，
        // 所以每次启动都把完整的字段落盘，用户打开文件就知道有哪些能改。
        s.Save();
        return s;
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
