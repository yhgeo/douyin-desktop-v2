using System.Text.Json.Serialization;

namespace DouyinDesktop.Core.Models;

/// <summary>一条评论。字段按 /aweme/v1/web/comment/list/ 的实际响应取。</summary>
public sealed class Comment
{
    [JsonPropertyName("cid")] public string Id { get; init; } = "";
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("create_time")] public long CreateTime { get; init; }
    [JsonPropertyName("digg_count")] public long DiggCount { get; init; }
    [JsonPropertyName("reply_comment_total")] public long ReplyCount { get; init; }
    [JsonPropertyName("ip_label")] public string IpLabel { get; init; } = "";
    [JsonPropertyName("label_text")] public string LabelText { get; init; } = "";
    [JsonPropertyName("user")] public Author? User { get; init; }
    [JsonPropertyName("reply_comment")] public List<Comment>? Replies { get; init; }

    [JsonIgnore] public string Nickname => User?.Nickname ?? "已注销用户";

    [JsonIgnore] public string LikeText => DiggCount > 0 ? $"♥ {DiggCount}" : "";

    [JsonIgnore] public bool HasReplies => ReplyCount > 0;

    [JsonIgnore] public string ReplyText => ReplyCount > 0 ? $"展开 {ReplyCount} 条回复" : "";

    [JsonIgnore] public string TimeText
    {
        get
        {
            if (CreateTime <= 0) return "";
            var t = DateTimeOffset.FromUnixTimeSeconds(CreateTime).ToLocalTime();
            var span = DateTimeOffset.Now - t;
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
            return t.ToString("yyyy-MM-dd");
        }
    }

    /// <summary>「广东 · 3 天前」这种副标题。</summary>
    [JsonIgnore]
    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(IpLabel)) parts.Add(IpLabel);
            if (!string.IsNullOrEmpty(TimeText)) parts.Add(TimeText);
            return string.Join(" · ", parts);
        }
    }
}

public sealed record CommentPage(
    IReadOnlyList<Comment> Comments, bool HasMore, long Cursor, long Total)
{
    public static readonly CommentPage Empty = new(Array.Empty<Comment>(), false, 0, 0);
}
