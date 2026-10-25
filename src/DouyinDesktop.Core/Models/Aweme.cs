using System.Text.Json;
using System.Text.Json.Serialization;

namespace DouyinDesktop.Core.Models;

/// <summary>
/// 一条作品。字段按 2026-10 实测的 /aweme/v1/web/module/feed/ 响应结构定义，
/// 只保留客户端真正会用到的那部分——多余的字段解析出来也没人看。
/// </summary>
public sealed class Aweme
{
    [JsonPropertyName("aweme_id")] public string Id { get; init; } = "";
    [JsonPropertyName("desc")] public string Desc { get; init; } = "";
    [JsonPropertyName("create_time")] public long CreateTime { get; init; }
    /// <summary>发布地（"广东""北京"这种），服务端按 IP 判断。</summary>
    [JsonPropertyName("ip_label")] public string IpLabel { get; init; } = "";
    /// <summary>0=视频，68=图文（图集）。诊断用，界面不显示。</summary>
    [JsonPropertyName("aweme_type")] public int AwemeType { get; init; }
    [JsonPropertyName("author")] public Author? Author { get; init; }
    [JsonPropertyName("statistics")] public Statistics? Statistics { get; init; }
    [JsonPropertyName("video")] public VideoInfo? Video { get; init; }
    [JsonPropertyName("music")] public MusicInfo? Music { get; init; }
    [JsonPropertyName("images")] public List<ImageInfo>? Images { get; init; }

    /// <summary>图集作品没有可播放的视频流，走另一套展示逻辑。</summary>
    [JsonIgnore] public bool IsImagePost => Images is { Count: > 0 };

    /// <summary>
    /// 当前登录用户是否点过赞（1 = 点过）。
    /// 用可空类型：很多接口根本不返回这个字段，null 表示"不知道"，
    /// 不能当成"没点过"——否则会误报"点赞没有生效"。
    /// </summary>
    [JsonPropertyName("user_digged")] public int? UserDigged { get; init; }

    /// <summary>文案里的话题标签（结构化）。覆盖率实测 84%，平均每条 3 个。</summary>
    [JsonPropertyName("text_extra")] public List<TextExtra>? TextExtra { get; init; }

    /// <summary>
    /// 平台给视频打的标签（不是作者加的）。
    /// 实测推荐流里 30 条全是 null，详情接口待验证——有的话能补上"作者没加标签"的那 14%。
    /// </summary>
    [JsonPropertyName("video_tag")] public List<VideoTag>? VideoTag { get; init; }

    /// <summary>平台标签名（不含 #）。</summary>
    [JsonIgnore]
    public IReadOnlyList<string> OfficialTags =>
        VideoTag is null
            ? Array.Empty<string>()
            : VideoTag.Where(x => x is not null && !string.IsNullOrEmpty(x.TagName))
                      .Select(x => x.TagName).ToArray();

    /// <summary>
    /// 话题名（不含 #），给标签过滤用。
    ///
    /// ⚠️ 两处都得判空，缺一个就是 NullReferenceException，而调用方是标签过滤——
    /// 表现是"收藏整个打不开"，日志里只有一句 Object reference（2026-10-26 踩过两次）：
    ///   1. 数组元素本身可能是 null（`[null, {...}]`）
    ///   2. **`hashtag_name` 可能是 JSON null**，会把属性默认值 `""` 覆盖成 null
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Hashtags =>
        TextExtra is null
            ? Array.Empty<string>()
            : TextExtra.Where(x => x is not null && !string.IsNullOrEmpty(x.HashtagName))
                       .Select(x => x.HashtagName).ToArray();

    /// <summary>能不能播：有 uri 才算。</summary>
    [JsonIgnore] public bool IsPlayable => !string.IsNullOrEmpty(Video?.PlayAddr?.Uri);

    [JsonIgnore] public string AuthorName => Author?.Nickname ?? "未知作者";

    [JsonIgnore] public DateTimeOffset CreatedLocal =>
        DateTimeOffset.FromUnixTimeSeconds(CreateTime).ToLocalTime();

    /// <summary>主页封面墙上的点赞数（"♥ 3.6万"）。</summary>
    [JsonIgnore]
    public string LikeShortText
        => Statistics is null ? "" : "♥ " + FormatCount(Statistics.DiggCount);

    public static string FormatCount(long n) => n switch
    {
        >= 100_000_000 => (n / 100_000_000.0).ToString("0.#") + "亿",
        >= 10_000 => (n / 10_000.0).ToString("0.#") + "万",
        _ => n.ToString(),
    };

    [JsonIgnore] public string DurationText
    {
        get
        {
            int total = (int)Math.Round((Video?.Duration ?? 0) / 1000.0);
            if (total <= 0) return "--:--";
            return $"{total / 60}:{total % 60:00}";
        }
    }
}

/// <summary>
/// 文案里的一段附加内容。type=1 是话题标签，hashtag_name 就是 # 后面的名字。
/// </summary>
/// <summary>平台给视频打的标签。tag_id 在 JSON 里可能是数字也可能是字符串，这里只取名字。</summary>
public sealed class VideoTag
{
    [JsonPropertyName("tag_name")] public string TagName { get; init; } = "";
}

public sealed class TextExtra
{
    [JsonPropertyName("hashtag_name")] public string HashtagName { get; init; } = "";
    [JsonPropertyName("hashtag_id")] public string HashtagId { get; init; } = "";
    [JsonPropertyName("type")] public int Type { get; init; }
    /// <summary>
    /// 是不是商业推广。
    ///
    /// 服务端有时给 true/false，有时给 0/1，所以配了宽松转换器。
    /// **不能用 JsonElement 接**：它持有 JsonDocument 的引用，文档一释放就失效，
    /// 保存现场时序列化会抛 `Operation is not valid due to the current state of the object`
    /// （2026-10-26 关闭程序弹窗就是这么来的）。
    /// </summary>
    [JsonPropertyName("is_commerce")]
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool IsCommerce { get; init; }
}

public sealed class Author
{
    [JsonPropertyName("uid")] public string Uid { get; init; } = "";
    [JsonPropertyName("sec_uid")] public string SecUid { get; init; } = "";
    [JsonPropertyName("nickname")] public string Nickname { get; init; } = "";
    [JsonPropertyName("signature")] public string Signature { get; init; } = "";
    [JsonPropertyName("avatar_thumb")] public UrlList? AvatarThumb { get; init; }

    /// <summary>
    /// 我与这个作者的关系：1 = 已关注，0 = 没关注。
    /// 用来判断"关注流"里返回的到底是不是关注的人（服务端会偷偷掺推荐）。
    /// </summary>
    [JsonPropertyName("follow_status")] public int FollowStatus { get; init; }

    [JsonIgnore] public string AvatarUrl => AvatarThumb?.First ?? "";
}

public sealed class Statistics
{
    [JsonPropertyName("digg_count")] public long DiggCount { get; init; }
    [JsonPropertyName("comment_count")] public long CommentCount { get; init; }
    [JsonPropertyName("share_count")] public long ShareCount { get; init; }
    [JsonPropertyName("collect_count")] public long CollectCount { get; init; }
    [JsonPropertyName("play_count")] public long PlayCount { get; init; }
}

public sealed class VideoInfo
{
    [JsonPropertyName("duration")] public long Duration { get; init; }
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }
    [JsonPropertyName("ratio")] public string Ratio { get; init; } = "";
    [JsonPropertyName("play_addr")] public PlayAddr? PlayAddr { get; init; }
    [JsonPropertyName("cover")] public UrlList? Cover { get; init; }
    [JsonPropertyName("bit_rate")] public List<BitRateInfo>? BitRates { get; init; }

    [JsonIgnore] public string CoverUrl => Cover?.First ?? "";

    /// <summary>宽高比，用于界面留黑边的计算；拿不到就给竖屏默认值。</summary>
    [JsonIgnore] public double Aspect => Width > 0 && Height > 0 ? (double)Width / Height : 9.0 / 16.0;

    /// <summary>接口给的最高码率。mpv 的 video-bitrate 对网络流不总上报，用它兜底。</summary>
    [JsonIgnore] public long MaxBitRate => BitRates is { Count: > 0 } ? BitRates.Max(x => x.BitRate) : 0;
}

/// <summary>清晰度档位。bit_rate 单位是 bps。</summary>
public sealed class BitRateInfo
{
    [JsonPropertyName("bit_rate")] public long BitRate { get; init; }
    [JsonPropertyName("gear_name")] public string GearName { get; init; } = "";
    [JsonPropertyName("quality_type")] public int QualityType { get; init; }
    [JsonPropertyName("is_h265")] public int IsH265 { get; init; }
    [JsonPropertyName("play_addr")] public PlayAddr? PlayAddr { get; init; }
}

/// <summary>
/// play_addr.url_list 是加密的 800 位 hex，不能直接用；
/// 只有 uri 有意义——拿它去换 aweme/v1/play/ 的 302 入口。
/// </summary>
public sealed class PlayAddr
{
    [JsonPropertyName("uri")] public string Uri { get; init; } = "";
    [JsonPropertyName("url_list")] public List<string>? UrlList { get; init; }
}

public sealed class UrlList
{
    [JsonPropertyName("url_list")] public List<string>? Urls { get; init; }

    [JsonIgnore] public string First => Urls is { Count: > 0 } ? Urls[0] : "";
}

public sealed class ImageInfo
{
    [JsonPropertyName("url_list")] public List<string>? UrlList { get; init; }
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }

    [JsonIgnore] public string Url => UrlList is { Count: > 0 } ? UrlList[0] : "";
}

public sealed class MusicInfo
{
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("author")] public string Author { get; init; } = "";
    [JsonPropertyName("play_url")] public UrlList? PlayUrl { get; init; }

    /// <summary>音频直链。图集没有视频流，但可以播这个当背景音乐。</summary>
    [JsonIgnore] public string AudioUrl => PlayUrl?.First ?? "";
}

/// <summary>
/// 宽松的布尔转换器：`true` / `false` / `1` / `0` / `"1"` / `"true"` 都认。
///
/// 为什么需要：抖音有些字段（比如 `text_extra[].is_commerce`）同一批数据里
/// 既有布尔又有数字，定义成 bool 会让**整条作品反序列化失败**——
/// 实测 20 条收藏里 6 条因此被丢掉，表现是"接口有 1.7MB 却说没有内容"。
///
/// 为什么不用 JsonElement 兜：它持有 JsonDocument 的引用，文档释放后取值就抛
/// `Operation is not valid due to the current state of the object`，
/// 而 Aweme 会被存进「接着上次看」的现场文件，序列化时直接崩（关闭程序弹窗的根因）。
/// </summary>
public sealed class LenientBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True: return true;
            case JsonTokenType.False: return false;
            case JsonTokenType.Number:
                return reader.TryGetInt64(out long n) ? n != 0 : false;
            case JsonTokenType.String:
                string? s = reader.GetString();
                return s is "1" or "true" or "True" or "TRUE";
            case JsonTokenType.Null:
            case JsonTokenType.None:
                return false;
            default:
                reader.Skip();
                return false;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}
