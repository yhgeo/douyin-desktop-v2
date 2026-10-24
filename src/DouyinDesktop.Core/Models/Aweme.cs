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

public sealed class Author
{
    [JsonPropertyName("uid")] public string Uid { get; init; } = "";
    [JsonPropertyName("sec_uid")] public string SecUid { get; init; } = "";
    [JsonPropertyName("nickname")] public string Nickname { get; init; } = "";
    [JsonPropertyName("signature")] public string Signature { get; init; } = "";
    [JsonPropertyName("avatar_thumb")] public UrlList? AvatarThumb { get; init; }

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
