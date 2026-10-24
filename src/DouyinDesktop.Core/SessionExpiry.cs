namespace DouyinDesktop.Core;

/// <summary>
/// 从 Cookie 里算出登录凭据的到期时间。
///
/// 抖音的 `sid_guard` 格式是：`sid_guard=&lt;sid&gt;|&lt;签发秒&gt;|&lt;有效期秒&gt;|&lt;日期&gt;`
/// 到期时间 = 签发时间 + 有效期。网页端每次访问都会把这个时间往后推，
/// 我们只拷一份 Cookie，所以到期时间是固定的。
///
/// ⚠️ 浏览器写进 Cookie 时竖线是 **URL 编码的 `%7C`**，
/// 以前的正则只认原始 `|`，所以永远匹配不到、一直显示"到期时间未知"。
/// </summary>
public static class SessionExpiry
{
    /// <summary>拿不到 sid_guard 就返回 null（那就没法预判，只能等过期检测）。</summary>
    public static DateTimeOffset? FromCookies(string? cookies)
    {
        if (string.IsNullOrEmpty(cookies)) return null;

        string? raw = null;
        foreach (string part in cookies.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string kv = part.Trim();
            if (kv.StartsWith("sid_guard=", StringComparison.Ordinal))
            {
                raw = kv["sid_guard=".Length..];
                break;
            }
        }
        if (string.IsNullOrEmpty(raw)) return null;

        // %7C → |（两种写法都认）
        raw = raw.Replace("%7C", "|").Replace("%7c", "|");

        string[] seg = raw.Split('|');
        if (seg.Length < 3) return null;

        if (!long.TryParse(seg[1], out long issued)) return null;
        if (!long.TryParse(seg[2], out long ttl)) return null;
        if (ttl <= 0) return null;

        try { return DateTimeOffset.FromUnixTimeSeconds(issued + ttl); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>是否已经进入"快过期"窗口。</summary>
    public static bool IsNearExpiry(string? cookies, TimeSpan window)
    {
        var at = FromCookies(cookies);
        return at is not null && at.Value - DateTimeOffset.Now <= window;
    }

    public static string Describe(string? cookies)
    {
        var at = FromCookies(cookies);
        if (at is null) return "有效期未知";

        var left = at.Value - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) return "已到期";

        string remain = left.TotalDays >= 1
            ? $"还剩 {(int)left.TotalDays} 天"
            : $"还剩 {(int)left.TotalHours} 小时";
        return $"有效期至 {at.Value.LocalDateTime:MM-dd}（{remain}）";
    }
}
