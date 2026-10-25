using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DouyinDesktop.Core.Models;

namespace DouyinDesktop.Core;

/// <summary>
/// 一条备份记录。
///
/// 设计重点是**"以后还能找回来"**，不是"把视频存下来"：
///   · <see cref="AwemeId"/> —— 作品唯一 ID，永久有效，随时能重新拉详情
///   · <see cref="VideoId"/>  —— <c>play_addr.uri</c>，也是永久标识，
///     用它走 <c>aweme/v1/play/?video_id=</c> 就能换出新的播放地址
///   · <see cref="WebUrl"/>   —— 网页版链接，作品还在就永远能打开
///   · <see cref="PlayUrlAtExport"/> —— 导出那一刻的 CDN 地址，**几小时后就失效**，
///     只作参考，真正要重播请用 VideoId 重取
///
/// 所以整个备份只有几百 KB，视频文件一个都不下载。
/// 唯一的失效场景是作者把作品删了——那种情况任何客户端都救不回来。
/// </summary>
public sealed class BackupItem
{
    public string AwemeId { get; init; } = "";
    public string VideoId { get; init; } = "";
    public string WebUrl { get; init; } = "";

    /// <summary>video / image。</summary>
    public string Type { get; init; } = "video";

    public string Desc { get; init; } = "";
    public long CreateTime { get; init; }
    public string CreateTimeText { get; init; } = "";

    public string AuthorName { get; init; } = "";
    public string AuthorSecUid { get; init; } = "";
    public string AuthorUid { get; init; } = "";

    public long Digg { get; init; }
    public long Comment { get; init; }
    public long Share { get; init; }
    public long Collect { get; init; }

    public long DurationMs { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>封面地址（带 token，也会过期，仅用于展示）。</summary>
    public string CoverUrl { get; init; } = "";

    public List<string> Hashtags { get; init; } = new();
    public List<string> OfficialTags { get; init; } = new();
    public List<string> Images { get; init; } = new();

    public string MusicTitle { get; init; } = "";
    public string MusicAuthor { get; init; } = "";

    /// <summary>导出时的播放地址，会过期。</summary>
    public string PlayUrlAtExport { get; init; } = "";
}

/// <summary>整个备份文件的元信息。</summary>
public sealed class BackupFile
{
    public string ExportedAt { get; init; } = "";
    /// <summary>collect / likes / follow。</summary>
    public string Source { get; init; } = "";
    /// <summary>收藏夹名（收藏来源才有）。</summary>
    public string CollectsName { get; init; } = "";
    public int Count { get; init; }
    public string Note { get; init; } = "";
    public List<BackupItem> Items { get; init; } = new();
}

/// <summary>
/// 把一批作品导出成 JSON / CSV / HTML。
///
/// 三种格式各有用途，**内容完全一致**，只是给不同场景看：
///   · JSON —— 完整字段，程序能再读回来（以后做"从备份重播"）
///   · CSV  —— Excel 打开筛选排序
///   · HTML —— 离线卡片墙，点封面跳原视频
/// </summary>
public static class BackupExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // 中文不转成 \uXXXX，备份文件要能直接读
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static BackupItem From(Aweme a)
    {
        var st = a.Statistics;
        return new BackupItem
        {
            AwemeId = a.Id,
            VideoId = a.Video?.PlayAddr?.Uri ?? "",
            WebUrl = $"https://www.douyin.com/video/{a.Id}",
            Type = a.IsImagePost ? "image" : "video",
            Desc = a.Desc ?? "",
            CreateTime = a.CreateTime,
            CreateTimeText = a.CreateTime > 0
                ? a.CreatedLocal.ToString("yyyy-MM-dd HH:mm")
                : "",
            AuthorName = a.Author?.Nickname ?? "",
            AuthorSecUid = a.Author?.SecUid ?? "",
            AuthorUid = a.Author?.Uid ?? "",
            Digg = st?.DiggCount ?? 0,
            Comment = st?.CommentCount ?? 0,
            Share = st?.ShareCount ?? 0,
            Collect = st?.CollectCount ?? 0,
            DurationMs = a.Video?.Duration ?? 0,
            Width = a.Video?.Width ?? 0,
            Height = a.Video?.Height ?? 0,
            CoverUrl = a.Video?.CoverUrl ?? "",
            Hashtags = a.Hashtags.ToList(),
            OfficialTags = a.OfficialTags.ToList(),
            Images = a.Images?.Select(x => x.Url).Where(x => x.Length > 0).ToList() ?? new List<string>(),
            MusicTitle = a.Music?.Title ?? "",
            MusicAuthor = a.Music?.Author ?? "",
            PlayUrlAtExport = a.Video?.PlayAddr?.UrlList is { Count: > 0 } urls ? urls[0] : "",
        };
    }

    public static BackupFile Build(IEnumerable<Aweme> items, string source, string collectsName = "")
    {
        var list = items.Select(From).ToList();
        return new BackupFile
        {
            ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Source = source,
            CollectsName = collectsName,
            Count = list.Count,
            Note = "播放地址会过期，请用 awemeId 或 videoId 重新取；网页链接只要作品没被删就永久有效。",
            Items = list,
        };
    }

    public static string ToJson(BackupFile file) => JsonSerializer.Serialize(file, JsonOpts);

    /// <summary>
    /// CSV。带 UTF-8 BOM，否则 Excel 打开中文全是乱码。
    /// </summary>
    public static string ToCsv(BackupFile file)
    {
        var sb = new StringBuilder();
        sb.Append('\uFEFF');

        sb.AppendLine("作品ID,网页链接,类型,文案,发布时间,作者,作者sec_uid,点赞,评论,分享,收藏," +
                      "时长(秒),分辨率,话题标签,平台标签,音乐,封面地址,视频ID");

        foreach (var it in file.Items)
        {
            sb.Append(Csv(it.AwemeId)).Append(',');
            sb.Append(Csv(it.WebUrl)).Append(',');
            sb.Append(Csv(it.Type == "image" ? "图集" : "视频")).Append(',');
            sb.Append(Csv(it.Desc)).Append(',');
            sb.Append(Csv(it.CreateTimeText)).Append(',');
            sb.Append(Csv(it.AuthorName)).Append(',');
            sb.Append(Csv(it.AuthorSecUid)).Append(',');
            sb.Append(it.Digg).Append(',');
            sb.Append(it.Comment).Append(',');
            sb.Append(it.Share).Append(',');
            sb.Append(it.Collect).Append(',');
            sb.Append(it.DurationMs > 0 ? (it.DurationMs / 1000.0).ToString("0.0") : "").Append(',');
            sb.Append(Csv(it.Width > 0 ? $"{it.Width}x{it.Height}" : "")).Append(',');
            sb.Append(Csv(string.Join(" ", it.Hashtags.Select(t => "#" + t)))).Append(',');
            sb.Append(Csv(string.Join(" / ", it.OfficialTags))).Append(',');
            sb.Append(Csv(it.MusicTitle.Length > 0 ? $"{it.MusicTitle} - {it.MusicAuthor}" : "")).Append(',');
            sb.Append(Csv(it.CoverUrl)).Append(',');
            sb.Append(Csv(it.VideoId));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Csv(string? s)
    {
        string v = s ?? "";
        if (v.Length == 0) return "";
        // 含逗号/引号/换行都要包起来，内部引号翻倍
        bool needQuote = v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r');
        v = v.Replace("\"", "\"\"");
        return needQuote ? $"\"{v}\"" : v;
    }

    /// <summary>离线卡片墙。封面走网络（会过期），但点卡片永远能跳原视频。</summary>
    public static string ToHtml(BackupFile file)
    {
        string title = file.Source switch
        {
            "collect" => $"我的收藏{folderSuffix(file.CollectsName)}",
            "likes" => "我的喜欢",
            "follow" => "关注流",
            _ => "抖音备份",
        };

        var sb = new StringBuilder();
        sb.Append($$"""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{H(title)}} · {{file.Count}} 条</title>
<style>
  :root{--bg:#0f0f14;--card:#17171e;--line:#242430;--text:#e8e8f0;--sub:#8c8c99;--accent:#fe2c55}
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--text);
       font:14px/1.6 "Microsoft YaHei UI","PingFang SC",system-ui,sans-serif}
  header{padding:28px 32px 18px;border-bottom:1px solid var(--line)}
  h1{margin:0;font-size:19px;font-weight:600}
  .meta{margin-top:8px;color:var(--sub);font-size:12px}
  .tip{margin-top:10px;color:#6a6a78;font-size:12px;line-height:1.7;max-width:860px}
  .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));
        gap:16px;padding:24px 32px 48px}
  a.card{display:block;background:var(--card);border:1px solid var(--line);
         border-radius:10px;overflow:hidden;text-decoration:none;color:inherit;
         transition:border-color .15s,transform .15s}
  a.card:hover{border-color:var(--accent);transform:translateY(-2px)}
  .thumb{position:relative;aspect-ratio:16/10;background:#0b0b0e;overflow:hidden}
  .thumb img{width:100%;height:100%;object-fit:cover;display:block}
  .thumb .badge{position:absolute;right:8px;bottom:8px;background:#000a;
                border-radius:4px;padding:1px 6px;font-size:11px;color:#ddd}
  .thumb .type{position:absolute;left:8px;top:8px;background:var(--accent);
               border-radius:4px;padding:1px 6px;font-size:11px;color:#fff}
  .body{padding:10px 12px 12px}
  .desc{font-size:12.5px;line-height:1.55;height:3.1em;overflow:hidden;
        display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical}
  .who{margin-top:8px;color:var(--sub);font-size:11.5px;
       white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
  .tags{margin-top:8px;display:flex;flex-wrap:wrap;gap:4px}
  .tags span{background:#22222c;color:#a8a8b8;border-radius:4px;
             padding:1px 6px;font-size:11px}
  .stats{margin-top:9px;color:#6a6a78;font-size:11px;display:flex;gap:12px}
  footer{padding:0 32px 40px;color:#55555f;font-size:11.5px}
</style>
</head>
<body>
<header>
  <h1>{{H(title)}}<span style="color:var(--sub);font-weight:400"> · {{file.Count}} 条</span></h1>
  <div class="meta">导出于 {{H(file.ExportedAt)}}{{H(file.Source == "collect" && file.CollectsName.Length > 0 ? " · 收藏夹「" + file.CollectsName + "」" : "")}}</div>
  <div class="tip">点任意卡片会在浏览器里打开原视频（链接永久有效，只要作品没被删）。<br>
  封面图地址带时效 token，过期后不显示属正常——不影响点进去看。</div>
</header>
<div class="grid">
""");

        foreach (var it in file.Items)
        {
            string dur = it.DurationMs > 0
                ? $"{it.DurationMs / 60000:0}:{it.DurationMs / 1000 % 60:00}"
                : "";

            sb.Append("  <a class=\"card\" href=\"").Append(H(it.WebUrl))
              .Append("\" target=\"_blank\" rel=\"noreferrer\">\n");

            sb.Append("    <div class=\"thumb\">");
            if (it.CoverUrl.Length > 0)
                sb.Append("<img loading=\"lazy\" src=\"").Append(H(it.CoverUrl))
                  .Append("\" alt=\"\" onerror=\"this.style.display='none'\">");
            if (it.Type == "image")
                sb.Append("<span class=\"type\">图集</span>");
            if (dur.Length > 0)
                sb.Append("<span class=\"badge\">").Append(H(dur)).Append("</span>");
            sb.Append("</div>\n");

            sb.Append("    <div class=\"body\">\n");
            string desc = it.Desc.Length > 0 ? it.Desc : "（无文案）";
            sb.Append("      <div class=\"desc\">").Append(H(desc)).Append("</div>\n");
            sb.Append("      <div class=\"who\">@").Append(H(it.AuthorName.Length > 0 ? it.AuthorName : "未知作者"));
            if (it.CreateTimeText.Length > 0)
                sb.Append("　").Append(H(it.CreateTimeText));
            sb.Append("</div>\n");

            // 优先显示平台标签（更规整），没有再用作者话题
            var tags = it.OfficialTags.Count > 0 ? it.OfficialTags : it.Hashtags.Take(3).ToList();
            if (tags.Count > 0)
            {
                sb.Append("      <div class=\"tags\">");
                foreach (string t in tags.Take(3))
                    sb.Append("<span>").Append(H(t)).Append("</span>");
                sb.Append("</div>\n");
            }

            sb.Append("      <div class=\"stats\"><span>♥ ").Append(Aweme.FormatCount(it.Digg))
              .Append("</span><span>💬 ").Append(Aweme.FormatCount(it.Comment))
              .Append("</span><span>↗ ").Append(Aweme.FormatCount(it.Share))
              .Append("</span></div>\n");

            sb.Append("    </div>\n  </a>\n");
        }

        sb.Append($$"""
</div>
<footer>由抖音桌面版导出 · {{H(file.Note)}}</footer>
</body>
</html>
""");
        return sb.ToString();
    }

    private static string folderSuffix(string name)
        => name.Length > 0 ? $"「{name}」" : "";

    /// <summary>HTML 转义。备份里什么字符都可能出现，不转义会直接破坏页面结构。</summary>
    private static string H(string? s) => (s ?? "")
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
