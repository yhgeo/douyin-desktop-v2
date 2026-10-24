using DouyinDesktop.Core;

namespace DouyinDesktop.Tests;

/// <summary>
/// 从 Cookie 里算凭据到期时间。
///
/// 这段逻辑曾经**永远返回 null**：正则只认原始竖线 `|`，
/// 而浏览器写进 Cookie 的是 URL 编码的 `%7C`，
/// 于是设置页一直显示"到期时间未知"，没人发现。
/// 现在挪到 Core 里，用真实格式钉住。
/// </summary>
public class SessionExpiryTests
{
    [Fact]
    public void 解析浏览器真实格式的百分号编码()
    {
        // 取自用户机器上 data/session.json 的真实片段
        const string cookies =
            "sid_guard=61274dd7245f8b48c36c490568c71faa%7C1792863742%7C5184000%7CWed%2C+23-Dec-2026; " +
            "sessionid=61274dd7245f8b48c36c490568c71faa";

        var at = SessionExpiry.FromCookies(cookies);

        Assert.NotNull(at);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792863742 + 5184000), at!.Value);
    }

    [Fact]
    public void 也认未编码的原始竖线()
    {
        var at = SessionExpiry.FromCookies("sid_guard=abc|1792863742|5184000|Wed, 23-Dec-2026");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1798047742), at!.Value);
    }

    [Fact]
    public void 没有sid_guard就返回null()
    {
        Assert.Null(SessionExpiry.FromCookies("sessionid=x; uid_tt=y"));
        Assert.Null(SessionExpiry.FromCookies(""));
        Assert.Null(SessionExpiry.FromCookies(null));
    }

    [Fact]
    public void 格式不完整就返回null()
    {
        Assert.Null(SessionExpiry.FromCookies("sid_guard=abc"));
        Assert.Null(SessionExpiry.FromCookies("sid_guard=abc%7C123"));
        Assert.Null(SessionExpiry.FromCookies("sid_guard=abc%7Cnotanumber%7C5184000"));
        Assert.Null(SessionExpiry.FromCookies("sid_guard=abc%7C123%7C0"));   // 有效期为 0
    }

    [Fact]
    public void 过期的Cookie能识别出来()
    {
        // 签发 2020-01-01，有效期 1 天 → 早就过期
        var at = SessionExpiry.FromCookies("sid_guard=x%7C1577836800%7C86400");
        Assert.NotNull(at);
        Assert.True(at!.Value < DateTimeOffset.Now);
        Assert.Equal("已到期", SessionExpiry.Describe("sid_guard=x%7C1577836800%7C86400"));
    }

    [Fact]
    public void 未过期时给出剩余天数()
    {
        long issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string cookies = $"sid_guard=x%7C{issued}%7C5184000";   // 60 天

        string text = SessionExpiry.Describe(cookies);
        Assert.Contains("有效期至", text);
        Assert.Contains("还剩", text);
    }
}
