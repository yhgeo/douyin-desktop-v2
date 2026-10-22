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

/// <summary>
/// 把弹幕列表变成 ASS 字幕。
///
/// 为什么走字幕而不是在 WPF 上画：视频是 HwndHost（独立 Win32 子窗口），
/// 按 airspace 规则它永远盖在 WPF 内容之上，WPF 画的弹幕根本看不见。
/// 交给 mpv 渲染成字幕轨，位置/时序都跟着视频走，暂停和拖动也自动同步。
/// </summary>
public static class DanmakuAss
{
    private const int LaneCount = 6;
    private const double ScreenSeconds = 8.0;   // 一条弹幕横穿屏幕用多久

    /// <summary>
    /// 生成 ASS。items 里的 OffsetMs 是**相对视频开头**的绝对时间，
    /// 所以分窗拉回来的也能拼在同一个脚本里。
    /// </summary>
    public static string Build(IReadOnlyList<DanmakuItem> items, int videoWidth, int videoHeight)
    {
        // PlayRes 跟着视频走，不然竖屏视频里坐标会全错位
        int w = videoWidth > 0 ? videoWidth : 1080;
        int h = videoHeight > 0 ? videoHeight : 1920;

        int fontSize = Math.Max(18, h / 38);
        int laneHeight = fontSize + h / 60;
        int top = h / 12;

        var sb = new StringBuilder(64 * 1024);
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine($"PlayResX: {w}");
        sb.AppendLine($"PlayResY: {h}");
        sb.AppendLine("WrapStyle: 2");          // 不自动换行，弹幕就该一行
        sb.AppendLine("ScaledBorderAndShadow: yes");
        sb.AppendLine("Collisions: None");      // 位置我们自己算，别让 libass 挪
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, " +
                      "OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, " +
                      "ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, " +
                      "MarginL, MarginR, MarginV, Encoding");
        sb.AppendLine($"Style: DM,Microsoft YaHei,{fontSize},&H00FFFFFF,&H00FFFFFF,&H00000000," +
                      $"&H64000000,0,0,0,0,100,100,0,0,1,2,1,7,0,0,0,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        // 简单防重叠：每条弹幕挑一个「最近最少用」的轨道
        var laneFreeAt = new long[LaneCount];

        foreach (var d in items.OrderBy(x => x.OffsetMs))
        {
            string text = Clean(d.Text);
            if (text.Length == 0) continue;

            int lane = 0;
            for (int i = 1; i < LaneCount; i++)
                if (laneFreeAt[i] < laneFreeAt[lane]) lane = i;

            // 同一轨道上一条还没走完一半就不许再放，否则会叠在一起
            long startMs = Math.Max(d.OffsetMs, laneFreeAt[lane]);
            long endMs = startMs + (long)(ScreenSeconds * 1000);
            laneFreeAt[lane] = startMs + (long)(ScreenSeconds * 1000 / 2);

            int y = top + lane * laneHeight;
            sb.Append("Dialogue: 0,")
              .Append(Time(startMs)).Append(',')
              .Append(Time(endMs)).Append(",DM,,0,0,0,,")
              .Append($"{{\\move({w + 20},{y},{-w},{y})}}")
              .Append(text)
              .AppendLine();
        }

        return sb.ToString();
    }

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
