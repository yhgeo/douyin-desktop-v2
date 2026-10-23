using System.Text.RegularExpressions;

namespace DouyinDesktop.App;

/// <summary>
/// 从 Cookie 里算出登录凭据的到期时间。
///
/// 抖音的 `sid_guard` 格式是：`sid_guard=&lt;sid&gt;|&lt;签发秒&gt;|&lt;有效期秒&gt;|&lt;...&gt;`
/// 到期时间 = 签发时间 + 有效期。网页端每次访问都会把这个时间往后推，
/// 我们只拷一份 Cookie，所以到期时间是固定的。
/// </summary>
public static class SessionExpiry
{
    private static readonly Regex SidGuard = new(
        @"sid_guard=[^|;]*\|(\d+)\|(\d+)\|", RegexOptions.Compiled);

    /// <summary>拿不到 sid_guard 就返回 null（那就没法预判，只能等过期检测）。</summary>
    public static DateTimeOffset? FromCookies(string? cookies)
    {
        if (string.IsNullOrEmpty(cookies)) return null;

        var m = SidGuard.Match(cookies);
        if (!m.Success) return null;

        if (!long.TryParse(m.Groups[1].Value, out long issued)) return null;
        if (!long.TryParse(m.Groups[2].Value, out long ttl)) return null;
        if (ttl <= 0) return null;

        try { return DateTimeOffset.FromUnixTimeSeconds(issued + ttl); }
        catch { return null; }
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
        if (at is null) return "到期时间未知";
        return at.Value <= DateTimeOffset.Now
            ? "已到期"
            : $"有效期至 {at.Value.LocalDateTime:MM-dd HH:mm}";
    }
}
