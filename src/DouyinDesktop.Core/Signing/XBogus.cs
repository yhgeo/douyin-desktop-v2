using System.Security.Cryptography;
using System.Text;

namespace DouyinDesktop.Core.Signing;

/// <summary>
/// x_bogus 签名。
///
/// 搜索接口（`/aweme/v1/web/search/item/`）**只认 x_bogus，不认 a_bogus** ——
/// 用 a_bogus 请求会返回 `status_code=2483 请先登录，再继续搜索吧`，
/// 参数补齐了也一样，所以这不是缺参数、是签名类型不对。
///
/// 算法移植自 https://github.com/smalls0098/xb （Go 实现，MIT）。
/// 结构很简单：19 字节的 list → RC4 → 自定义字母表的 base64，没有浏览器指纹参与。
///
/// 已验证：仓库自带的参考向量能逐字符对上（见 XBogusTests）。
/// </summary>
public static class XBogus
{
    private const string Alphabet =
        "Dkdpgh4ZKsQB80/Mfvw36XI1R25-WUAlEi7NLboqYTOPuzmFjJnryx9HVGcaStCe";

    private static readonly byte[] UaKey = { 0, 1, 14 };
    private static readonly byte[] ListKey = { 255 };
    private const uint Fixed = 3845494467;

    /// <summary>
    /// 生成 x_bogus。
    /// </summary>
    /// <param name="query">URL 里的 query 串（不含 ?，不含 a_bogus/x_bogus 本身）</param>
    /// <param name="postData">POST body，GET 请求传空串</param>
    /// <param name="userAgent">与请求头发出去的那个完全一致</param>
    /// <param name="timestamp">Unix 秒；和 URL 里的 timestamp 参数要一致</param>
    public static string Sign(string query, string postData, string userAgent, uint? timestamp = null)
    {
        uint ts = timestamp ?? (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        byte[] md5Params = Md5(Md5(Encoding.UTF8.GetBytes(query)));
        byte[] md5Post = Md5(Md5(Encoding.UTF8.GetBytes(postData)));

        // UA 先 RC4（key=[0,1,14]）再标准 base64，再取 MD5
        string uaB64 = Convert.ToBase64String(Rc4(UaKey, Encoding.UTF8.GetBytes(userAgent)));
        byte[] md5Ua = Md5(Encoding.UTF8.GetBytes(uaB64));

        // list = [64] + uaKey(3) + params[14:16] + post[14:16] + ua[14:16] + ts(4) + fixed(4) + xor(1)
        var list = new byte[19];
        int p = 0;

        list[p++] = 64;
        UaKey.CopyTo(list, p); p += UaKey.Length;
        list[p++] = md5Params[14];
        list[p++] = md5Params[15];
        list[p++] = md5Post[14];
        list[p++] = md5Post[15];
        list[p++] = md5Ua[14];
        list[p++] = md5Ua[15];

        WriteBigEndian(list, ref p, ts);
        WriteBigEndian(list, ref p, Fixed);

        // 最后一个字节是前 18 字节的异或
        byte xor = 0;
        for (int i = 0; i < 18; i++) xor ^= list[i];
        list[18] = xor;

        // enc = [2] + [255] + RC4(list)
        byte[] body = Rc4(ListKey, list);
        var enc = new byte[2 + body.Length];
        enc[0] = 2;
        enc[1] = 255;
        body.CopyTo(enc, 2);

        return CustomBase64(enc);
    }

    private static byte[] Md5(byte[] data) => MD5.HashData(data);

    private static void WriteBigEndian(byte[] buf, ref int pos, uint value)
    {
        buf[pos++] = (byte)(value >> 24);
        buf[pos++] = (byte)(value >> 16);
        buf[pos++] = (byte)(value >> 8);
        buf[pos++] = (byte)value;
    }

    /// <summary>标准 RC4（Go 的 crypto/rc4 同款）。</summary>
    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;

        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var result = new byte[data.Length];
        int a = 0;
        j = 0;

        for (int k = 0; k < data.Length; k++)
        {
            a = (a + 1) & 0xFF;
            j = (j + s[a]) & 0xFF;
            (s[a], s[j]) = (s[j], s[a]);
            result[k] = (byte)(data[k] ^ s[(s[a] + s[j]) & 0xFF]);
        }

        return result;
    }

    /// <summary>用抖音那套自定义字母表的 base64（不是标准表）。</summary>
    private static string CustomBase64(byte[] data)
    {
        var sb = new StringBuilder((data.Length + 2) / 3 * 4);

        for (int i = 0; i < data.Length; i += 3)
        {
            int b0 = data[i];
            int b1 = i + 1 < data.Length ? data[i + 1] : 0;
            int b2 = i + 2 < data.Length ? data[i + 2] : 0;

            sb.Append(Alphabet[b0 >> 2]);
            sb.Append(Alphabet[((b0 & 0x03) << 4) | (b1 >> 4)]);

            if (i + 1 < data.Length) sb.Append(Alphabet[((b1 & 0x0F) << 2) | (b2 >> 6)]);
            else sb.Append('=');

            if (i + 2 < data.Length) sb.Append(Alphabet[b2 & 0x3F]);
            else sb.Append('=');
        }

        return sb.ToString();
    }
}
