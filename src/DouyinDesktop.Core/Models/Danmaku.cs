using System.Text;
using System.Text.Json.Serialization;

namespace DouyinDesktop.Core.Models;

/// <summary>一条弹幕。字段按 /aweme/v1/web/danmaku/get_v2/ 的实际响应取。</summary>
public sealed class DanmakuItem
{
    [JsonPropertyName("danmaku_id")] public string Id { get; init; } = "";
    [JsonPropertyName("item_id")] public string ItemId { get; init; } = "";

    /// <summary>出现时间，毫秒（相对视频开头）。</summary>
    [JsonPropertyName("offset_time")] public long OffsetMs { get; init; }

    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("digg_count")] public long DiggCount { get; init; }
}

/// <summary>
/// 一个时间窗的弹幕。接口是**按 32 秒分段**返回的：
/// 请求一次只给 [start_time, end_time) 这一段，所以长视频要多次拉。
/// </summary>
public sealed record DanmakuPage(
    IReadOnlyList<DanmakuItem> Items, long StartMs, long EndMs)
{
    public static readonly DanmakuPage Empty = new(Array.Empty<DanmakuItem>(), 0, 0);

    public bool HasItems => Items.Count > 0;
}

/// <summary>弹幕显示区域。</summary>
public enum DanmakuArea
{
    Top,
    Middle,
    Bottom,
    Full,
}

/// <summary>
/// 弹幕外观参数。都是相对量，换分辨率不用重调。
/// </summary>
public sealed record DanmakuStyle
{
    /// <summary>字号占画面高度的百分比（2.0 - 6.0）。</summary>
    public double FontPercent { get; init; } = 3.2;

    /// <summary>不透明度 0-100。太高会挡住画面，太低看不清。</summary>
    public int Opacity { get; init; } = 90;

    /// <summary>描边宽度（相对字号的比例，0 = 不描边）。</summary>
    public double OutlineRatio { get; init; } = 0.10;

    /// <summary>
    /// 一条弹幕从进入到完全离开用几秒（4 - 20）。
    ///
    /// 下限不能太低：libass 对移动中的字幕是**逐帧按亚像素重新排版**的，
    /// 一帧位移越大，字符边缘的抖动越明显，看起来就像糊了一层。
    /// 实测 3 秒（一帧要挪近 40px）会明显发虚，8 秒以上就稳了。
    /// </summary>
    public double SpeedSeconds { get; init; } = 10;

    public DanmakuArea Area { get; init; } = DanmakuArea.Top;

    public static readonly DanmakuStyle Default = new();
}

/// <summary>
/// 把弹幕列表变成 ASS 字幕。
///
/// 为什么走字幕而不是在 WPF 上画：视频是 HwndHost（独立 Win32 子窗口），
/// 按 airspace 规则它永远盖在 WPF 内容之上，WPF 画的弹幕根本看不见。
/// 交给 mpv 渲染成字幕轨，位置/时序都跟着视频走，暂停和拖动也自动同步。
/// </summary>
public static class DanmakuAss
{
    /// <summary>
    /// 生成 ASS。items 里的 OffsetMs 是**相对视频开头**的绝对时间，
    /// 所以分窗拉回来的也能拼在同一个脚本里。
    /// </summary>
    public static string Build(
        IReadOnlyList<DanmakuItem> items, int videoWidth, int videoHeight,
        DanmakuStyle? style = null)
    {
        style ??= DanmakuStyle.Default;

        // PlayRes 不能直接用视频分辨率：小视频（540x960 这种）如果按原尺寸做画布，
        // libass 会先在小画布上栅格化文字，再被放大到窗口尺寸——静止时就有点糊，
        // 动起来（\move 每帧重定位）更明显。
        // 所以画布统一抬到 1080p / 1920 这一档，比这还大的视频才按原尺寸。
        int w = videoWidth > 0 ? videoWidth : 1080;
        int h = videoHeight > 0 ? videoHeight : 1920;

        int baseH = h >= w ? 1920 : 1080;
        int playH = Math.Max(h, baseH);
        int playW = (int)Math.Round((double)playH * w / h);

        double fontPct = Math.Clamp(style.FontPercent, 1.5, 8.0);
        int fontSize = Math.Max(12, (int)Math.Round(playH * fontPct / 100.0));

        double outlineRatio = Math.Clamp(style.OutlineRatio, 0, 0.3);
        double outline = Math.Round(fontSize * outlineRatio, 1);

        // 行高留 1.7 倍，不然上下两条会粘在一起
        int laneHeight = (int)Math.Round(fontSize * 1.7);

        var (areaTop, areaBottom) = AreaRange(style.Area, playH);
        int usable = Math.Max(laneHeight, areaBottom - areaTop);
        int laneCount = Math.Clamp(usable / laneHeight, 1, 8);

        // ASS 的 alpha 是反的：00 完全不透明，FF 全透明
        int opacity = Math.Clamp(style.Opacity, 10, 100);
        int alpha = (int)Math.Round((100 - opacity) / 100.0 * 255);
        string primary = $"&H{alpha:X2}FFFFFF";

        double speed = Math.Clamp(style.SpeedSeconds, 4, 20);
        long moveMs = (long)(speed * 1000);

        // 移动速度换算成「像素 / 毫秒」，用来算同一轨道上要隔多久才能放下一条
        double pxPerMs = (playW * 2.0 + 20) / moveMs;

        var sb = new StringBuilder(64 * 1024);
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine($"PlayResX: {playW}");
        sb.AppendLine($"PlayResY: {playH}");
        sb.AppendLine("WrapStyle: 2");          // 不自动换行，弹幕就该一行
        sb.AppendLine("ScaledBorderAndShadow: yes");
        sb.AppendLine("Collisions: None");      // 位置我们自己算，别让 libass 挪
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, " +
                      "OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, " +
                      "ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, " +
                      "MarginL, MarginR, MarginV, Encoding");
        // Shadow 给 0：1px 阴影在缩小显示时只剩半像素，糊成一圈灰晕，
        // 弹幕一动就像拖影。描边已经足够把白字从画面上分出来了。
        sb.AppendLine($"Style: DM,Microsoft YaHei,{fontSize},{primary},{primary},&H00000000," +
                      $"&H64000000,0,0,0,0,100,100,0,0,1,{outline:0.#},0,7,0,0,0,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        // 简单防重叠：每条弹幕挑一个「最近最少用」的轨道
        var laneFreeAt = new long[laneCount];
        long lastStart = long.MinValue;

        foreach (var d in items.OrderBy(x => x.OffsetMs))
        {
            string text = Clean(d.Text);
            if (text.Length == 0) continue;

            int lane = 0;
            for (int i = 1; i < laneCount; i++)
                if (laneFreeAt[i] < laneFreeAt[lane]) lane = i;

            // 同一轨道要等上一条走出「自身宽度 + 一点空隙」才能放下一条，
            // 否则两条会叠着走。
            long startMs = Math.Max(d.OffsetMs, laneFreeAt[lane]);

            // 全局再错开一点：同一秒来一堆弹幕时，如果都按原时间入场，
            // 屏幕上会像一堵墙一样整块推过来；错开之后就是一条斜着流动的队列。
            if (lastStart != long.MinValue) startMs = Math.Max(startMs, lastStart + 90);

            // 排得太晚就不要了——弹幕已经过时，硬塞反而挡住画面
            if (startMs - d.OffsetMs > 4000) continue;

            long endMs = startMs + moveMs;
            double estWidth = EstimateWidth(text, fontSize);
            laneFreeAt[lane] = startMs + (long)((estWidth + fontSize * 1.2) / pxPerMs);
            lastStart = startMs;

            int y = areaTop + lane * laneHeight;
            sb.Append("Dialogue: 0,")
              .Append(Time(startMs)).Append(',')
              .Append(Time(endMs)).Append(",DM,,0,0,0,,")
              .Append($"{{\\move({playW + 20},{y},{-playW},{y})}}")
              .Append(text)
              .AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 估个宽度，只用来决定同轨道隔多久放下一条。
    /// 中日韩字符按一个字宽算，其它（字母/数字/标点）按半个算——够用了。
    /// </summary>
    private static double EstimateWidth(string text, int fontSize)
    {
        double units = 0;
        foreach (char c in text) units += c < 0x2E80 ? 0.55 : 1.0;
        return Math.Max(units, 1) * fontSize;
    }

    /// <summary>各区域对应的纵向可用范围（像素，相对 PlayRes）。</summary>
    private static (int Top, int Bottom) AreaRange(DanmakuArea area, int h) => area switch
    {
        // 顶部 = 画面上方五分之一
        DanmakuArea.Top => (h * 2 / 100, h * 20 / 100),
        DanmakuArea.Middle => (h * 40 / 100, h * 62 / 100),
        DanmakuArea.Bottom => (h * 78 / 100, h * 97 / 100),
        _ => (h * 2 / 100, h * 97 / 100),
    };

    /// <summary>ASS 的时间格式：0:00:07.61</summary>
    private static string Time(long ms)
    {
        long total = Math.Max(0, ms);
        long h = total / 3_600_000;
        long m = total % 3_600_000 / 60_000;
        long s = total % 60_000 / 1000;
        long cs = total % 1000 / 10;
        return $"{h}:{m:00}:{s:00}.{cs:00}";
    }

    /// <summary>ASS 里 { } 是特效标记、换行要写成 \N，都得转义掉。</summary>
    private static string Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            sb.Append(c switch
            {
                '{' => '（',
                '}' => '）',
                '\r' => ' ',
                '\n' => ' ',
                _ => c,
            });
        }
        return sb.ToString().Trim();
    }
}
