using System.Text;

namespace DouyinDesktop.Core.Api;

/// <summary>
/// 与 Python 的 urllib.parse.quote(s, safe="") 行为一致。
/// 不能用 Uri.EscapeDataString——它保留的字符集和签名输入要求的不是一套。
/// </summary>
public static class UrlCodec
{
    public static string Quote(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var sb = new StringBuilder(bytes.Length + 8);
        foreach (byte b in bytes)
        {
            char c = (char)b;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c == '_' || c == '.' || c == '-' || c == '~')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    public static string ToQuery(IEnumerable<KeyValuePair<string, string>> ps)
        => string.Join("&", ps.Select(p => p.Key + "=" + Quote(p.Value)));
}
