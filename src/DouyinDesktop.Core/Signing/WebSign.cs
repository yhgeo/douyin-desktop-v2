using System.Security.Cryptography;
using System.Text;

namespace DouyinDesktop.Core.Signing;

/// <summary>
/// 抖音主站的 <c>x-secsdk-web-signature</c>（secsdk 的 webSign 策略）。
///
/// 只有一部分接口需要它：作者主页作品、作品详情、收藏、合集等。
/// 推荐流和评论不需要——所以之前没撞上这个。
///
/// 算法本身很简单：
///   MD5("{uifid}_{ts}_{固定盐}_{规范化query}&amp;timestamp={ts}") → 32 位小写 hex
///
/// 麻烦在"规范化"：value 要先解码、再用 encodeURIComponent 重新编码，
/// key 只解码不重编码。服务端按收到的 query 校验，所以必须发规范化后的串。
/// </summary>
public static class WebSign
{
    /// <summary>VM 常量池里写死的盐，与账号/会话无关。</summary>
    private const string Salt = "A96D855A08C0A9707F8BEF0D9A527E4E";

    public const string SignatureKey = "x-secsdk-web-signature";
    public const string TimestampKey = "timestamp";

    /// <summary>这些路径需要加 webSign（来自 SDK 的策略配置，GET 部分）。</summary>
    private static readonly HashSet<string> ProtectedPaths = new(StringComparer.Ordinal)
    {
        "/aweme/v1/web/aweme/detail/",
        "/aweme/v1/web/aweme/post/",
        "/aweme/v1/web/aweme/favorite/",
        "/aweme/v1/web/aweme/listcollection/",
        "/aweme/v1/web/mix/aweme/",
        "/aweme/v1/web/tab/feed/",
        "/aweme/v1/web/mix/list/",
        "/aweme/v1/web/music/aweme/",
        "/aweme/v1/web/music/list/",
        "/aweme/v1/web/mix/detail/",
        "/aweme/v1/web/mix/listcollection/",
        "/aweme/v1/web/music/detail/",
        "/aweme/v1/web/collects/list/",
        "/aweme/v1/web/collects/video/list/",
        "/aweme/v1/web/comment/list/",
    };

    public static bool IsProtected(string path) => ProtectedPaths.Contains(path);

    /// <summary>拼成可直接发送的完整 URL（query 已规范化，带 timestamp 与签名）。</summary>
    public static string SignUrl(string baseUrl, string query, string? uifid = null, long? timestamp = null)
    {
        var (signedQuery, signature, _) = Sign(query, uifid, timestamp);
        return $"{baseUrl}?{signedQuery}&{SignatureKey}={signature}";
    }

    /// <summary>返回（应当发送的规范化 query、签名、时间戳）。</summary>
    public static (string Query, string Signature, long Timestamp) Sign(
        string query, string? uifid = null, long? timestamp = null)
    {
        long ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string canon = Canonicalize(query);

        // query 里没有 uifid 时，SDK 会从 UIFID cookie 取值追加到末尾
        bool hasUifid = canon.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Any(p => p.StartsWith("uifid=", StringComparison.Ordinal));
        if (!hasUifid && !string.IsNullOrEmpty(uifid))
        {
            string pair = "uifid=" + EncodeComponent(uifid);
            canon = canon.Length == 0 ? pair : canon + "&" + pair;
        }

        string signedQuery = canon.Length == 0
            ? $"{TimestampKey}={ts}"
            : $"{canon}&{TimestampKey}={ts}";

        // 参与拼明文的 uifid 是解码后的原值
        string uifidValue = "";
        foreach (string pair in signedQuery.Split('&'))
        {
            if (!pair.StartsWith("uifid=", StringComparison.Ordinal)) continue;
            uifidValue = UnquotePlus(pair[6..]);
            break;
        }
        if (uifidValue.Length == 0 && !string.IsNullOrEmpty(uifid)) uifidValue = uifid;

        string plain = $"{uifidValue}_{ts}_{Salt}_{signedQuery}";
        string signature = Convert.ToHexString(
            MD5.HashData(Encoding.UTF8.GetBytes(plain))).ToLowerInvariant();

        return (signedQuery, signature, ts);
    }

    /// <summary>
    /// 规范化 query：保持原始顺序，不排序；key 只解码，value 解码后重编码。
    /// 没有等号的裸参数补成 <c>k=</c>。
    /// </summary>
    public static string Canonicalize(string query)
    {
        var parts = new List<string>();
        foreach (string pair in query.Split('&'))
        {
            if (pair.Length == 0) continue;

            int eq = pair.IndexOf('=');
            string key = eq >= 0 ? pair[..eq] : pair;
            string value = eq >= 0 ? pair[(eq + 1)..] : "";

            parts.Add(UnquotePlus(key) + "=" + EncodeComponent(UnquotePlus(value)));
        }
        return string.Join("&", parts);
    }

    /// <summary>
    /// 等价于 JS 的 encodeURIComponent：
    /// 不编码 A-Za-z0-9 和 <c>- _ . ! ~ * ' ( )</c>，其余按 UTF-8 百分号编码。
    /// </summary>
    private static string EncodeComponent(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (byte b in bytes)
        {
            char c = (char)b;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c is '-' or '_' or '.' or '!' or '~' or '*' or '\'' or '(' or ')')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>等价于 Python 的 unquote_plus：<c>+</c> 变空格，再解 <c>%XX</c>。</summary>
    private static string UnquotePlus(string s)
    {
        if (s.IndexOf('%') < 0 && s.IndexOf('+') < 0) return s;
        try { return Uri.UnescapeDataString(s.Replace("+", " ")); }
        catch (UriFormatException) { return s; }
    }
}
