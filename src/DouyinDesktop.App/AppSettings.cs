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
    /// ⚠️ 2026-10-25 实测已失效：module/feed 与 tab/feed 合计 80 条，
    /// images 字段一条都没有（用户实际现场 16 条也是 0）。
    /// 代码保留是因为接口随时可能变回来，但界面上要如实提示"大概率拉不到"。
    /// 图集的可靠入口是作者主页（aweme/post）。
    /// </summary>
    public bool ImageOnly { get; set; }

    /// <summary>详情页是否显示快捷键提示（可以手动折叠）。</summary>
    public bool ShowShortcuts { get; set; } = true;

    /// <summary>
    /// 标签过滤规则，一行一条。不带前缀 = 只看这类，"-" 前缀 = 屏蔽这类。
    ///
    /// 例：
    ///   助眠          → 只保留带"助眠"标签的
    ///   -短剧         → 带"短剧"的全丢
    ///   -游戏
    ///
    /// 匹配是包含关系（"短剧"能命中"热播短剧"）。白名单为空时只按黑名单过滤。
    /// 抖音标签覆盖率实测 84%，所以这个开关是有意义的；剩下 16% 没标签的，
    /// 在白名单模式下会被丢掉（判断不了）。
    /// </summary>
    public string TagRules { get; set; } = "";

    /// <summary>
    /// 标签分组（INI 风格，见 <see cref="DouyinDesktop.Core.TagGroups"/>）：
    /// <code>
    /// [助眠]
    /// 助眠
    /// 解压
    ///
    /// [技术]
    /// 单片机
    /// -短剧
    /// </code>
    /// </summary>
    public string TagGroupText { get; set; } = "";

    /// <summary>当前生效的组名（空 = 用第一组）。</summary>
    public string ActiveTagGroup { get; set; } = "";

    /// <summary>
    /// 每批拉多少条。**固定 10**，不再开放给用户调——
    /// 过滤导致条数不够时由加载逻辑自己多拉几轮（见 LoadFeedAsync）。
    ///
    /// 抖音接口对 count 没硬上限（实测 80 条能返回 77 条），但每条 JSON 约 150KB。
    /// 拉的是**纯信息**（视频地址、标签、统计），视频文件只在播放/预取时才下载。
    /// </summary>
    public int FeedBatchSize { get; set; } = 10;

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

        // 老版本只有单套 TagRules，没有分组。迁移成"默认"组，别让用户白配一遍。
        if (s.TagGroupText.Length == 0 && s.TagRules.Trim().Length > 0)
            s.TagGroupText = s.TagRules;

        // 写回一次，保证文件里字段完整（老配置文件缺新字段时会被补齐）。
        // 弹幕外观在 设置 → 播放 里有滑杆，也能直接手改这个文件。
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
