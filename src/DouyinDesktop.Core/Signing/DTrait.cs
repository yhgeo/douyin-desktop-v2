using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DouyinDesktop.Core.Signing;

/// <summary>
/// `x-tt-session-dtrait` 请求头的纯算实现。
///
/// 抖音对高风控接口（评论、点赞、详情、作者作品……）要求带这个头，
/// 缺了会被静默丢弃——表现就是"返回 status_code=0 但什么都没发生"，
/// 或者干脆返回空 body。
///
/// 结构：
///   {pk1_version}_{base64(RSA_PKCS1v15(pk1, aes_key_hex))}_{base64(iv + AES128CBC(payload))}
///
/// - aes_key_hex：16 字节随机数的 32 位小写十六进制**字符串**，RSA 加密的就是这串字符
/// - AES-128-CBC + PKCS7，IV 随机 16 字节并前置到密文
/// - payload：{"dtrait":&lt;设备特征 blob&gt;,"timestamp":&lt;秒&gt;,"sdkVersion":"1.0.0.16","path":&lt;pathname&gt;}
///
/// 内层 blob = [1 字节头][bool 位图][34 条字符串特征（每条 tag + murmur3 4 字节大端）]，
/// 渲染类特征（canvas/WebGL/音频/字体）只能真机采样，这里沿用随包的设备档案常量。
/// </summary>
public static class DTrait
{
    /// <summary>zero.js 里写死的 SDK 版本，进 payload。</summary>
    private const string SdkVersion = "1.0.0.16";

    /// <summary>浏览器登录包里内置的兜底公钥（pk1_version = d0）。</summary>
    private const string BuiltinPk1B64 =
        "LS0tLS1CRUdJTiBSU0EgUFVCTElDIEtFWS0tLS0tCk1JSUJDZ0tDQVFFQTQrZHZ2WTd1" +
        "TStvcGMrbkxHL0R1bVNlRm83YVZjSW0xTE8rbVVJcldwclJ6UDBhMUdwRVEKNHF0TzlN" +
        "UmYvbHdFSXgzOCs0Qlo0WE9HemV2VnR1VXZmSU9VRTdBVHRRVzdGS0pmNVBuU0xDSTYv" +
        "azB2bDFGQwpMVVNWbUVQNnFQSnJJalo0elhvcWkzeXVOWisxb2RiUkEvL0dIZ2NnU3l5" +
        "eWFMcXp3amtwV0dYb3VNWW12WXNTCnBway9mdjJFV0FCc3RQTnhXYTRFT0JDYWRUVVBr" +
        "WE5RNzZOQkVQOXh6ZkpTMjB3aUR2MW9TL3ZLdnJTVXBXY0oKbmF6a2tCdnFRYmJBcVZi" +
        "UUZURi9EUGlrcHB1NlpUNmxHSVh2SktDcmVlRmlIQTJxSzZ0UzE4U1dWSFc5QVJ6MQor" +
        "cGpCMWVxSUlZdG9oV3BUMkI0ME9DNE84dFZlQkFuYmlRSURBUUFCCi0tLS0tRU5EIFJT" +
        "QSBQVUJMSUMgS0VZLS0tLS0=";

    private const string Pk1Version = "d0";

    /// <summary>渲染类特征：只能真机采样，用档案常量。</summary>
    private static readonly int[] RenderFeatures =
        { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 13, 20, 21, 22, 23, 24, 25, 26, 33 };

    private static readonly Dictionary<int, uint> RenderHashes = new()
    {
        [1] = 755324578u, [2] = 3795871487u, [3] = 3493564185u, [4] = 1207649148u,
        [5] = 1383342405u, [6] = 762554969u, [7] = 2246935287u, [8] = 2868190146u,
        [9] = 2046748658u, [10] = 500342467u, [13] = 1189848898u, [20] = 2347868216u,
        [21] = 699560564u, [22] = 308954736u, [23] = 2240922934u, [24] = 466459917u,
        [25] = 1482679892u, [26] = 3117199483u, [33] = 1555653270u,
    };

    private static readonly Dictionary<int, bool> Bools = new()
    {
        [1] = false, [2] = false, [3] = false, [4] = true, [5] = false,
        [6] = true, [7] = true, [8] = false, [9] = false, [10] = false,
    };

    private static string? _cachedBlob;
    private static string? _cachedProfileKey;

    /// <summary>
    /// 生成 x-tt-session-dtrait。
    /// </summary>
    /// <param name="path">请求的 pathname，不含 query（如 /aweme/v1/web/comment/list/）</param>
    /// <param name="ua">当前 User-Agent，必须和实际发出去的一致</param>
    /// <param name="screenW">屏幕宽</param>
    /// <param name="screenH">屏幕高</param>
    /// <param name="cores">CPU 核数</param>
    /// <param name="memory">设备内存（GB）</param>
    public static string BuildHeader(string path, string ua, int screenW, int screenH, int cores, int memory)
        => BuildHeader(path, ua, screenW, screenH, cores, memory, null, null);

    /// <summary>
    /// 用服务端下发的公钥生成头。
    ///
    /// 内置的 d0 公钥只对**未登录的新档案**有效；登录态下服务端会下发 d1，
    /// 头的前缀必须是 d1 且用对应公钥加密，否则验签失败。
    /// </summary>
    public static string BuildHeader(string path, string ua, int screenW, int screenH,
        int cores, int memory, string? pk1Pem, string? pk1Version)
    {
        string blob = GetBlob(ua, screenW, screenH, cores, memory);

        byte[] keyBytes = RandomNumberGenerator.GetBytes(16);
        string keyHex = Convert.ToHexString(keyBytes).ToLowerInvariant();
        byte[] iv = RandomNumberGenerator.GetBytes(16);

        string payload = JsonSerializer.Serialize(new
        {
            dtrait = blob,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            sdkVersion = SdkVersion,
            path,
        });

        byte[] cipher;
        using (var aes = Aes.Create())
        {
            aes.Key = keyBytes;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            byte[] plain = Encoding.UTF8.GetBytes(payload);
            using var enc = aes.CreateEncryptor();
            cipher = enc.TransformFinalBlock(plain, 0, plain.Length);
        }

        byte[] part2 = new byte[iv.Length + cipher.Length];
        iv.CopyTo(part2, 0);
        cipher.CopyTo(part2, iv.Length);

        string pem = string.IsNullOrWhiteSpace(pk1Pem)
            ? Encoding.ASCII.GetString(Convert.FromBase64String(BuiltinPk1B64))
            : pk1Pem;
        string version = string.IsNullOrWhiteSpace(pk1Version) ? Pk1Version : pk1Version;

        byte[] encKey;
        using (var rsa = RSA.Create())
        {
            rsa.ImportFromPem(pem);
            encKey = rsa.Encrypt(Encoding.ASCII.GetBytes(keyHex), RSAEncryptionPadding.Pkcs1);
        }

        return $"{version}_{Convert.ToBase64String(encKey)}_{Convert.ToBase64String(part2)}";
    }

    /// <summary>调试用：直接拿到设备特征 blob，和参考实现逐字节比对。</summary>
    public static string BuildBlob(string ua, int screenW, int screenH, int cores, int memory)
        => GetBlob(ua, screenW, screenH, cores, memory);

    /// <summary>设备特征 blob（同一台设备可以复用，按 UA/分辨率缓存）。</summary>
    private static string GetBlob(string ua, int screenW, int screenH, int cores, int memory)
    {
        string key = $"{ua}|{screenW}x{screenH}|{cores}|{memory}";
        if (_cachedBlob is not null && _cachedProfileKey == key) return _cachedBlob;

        var values = new Dictionary<int, uint>(RenderHashes);
        values[34] = 19522071u;     // hook_score 的采样值

        foreach (var (n, s) in ComputedFeatures(ua, screenW, screenH, cores, memory))
            values[n] = Murmur3_32(s);

        var body = new List<byte>(34 * 5);
        for (int n = 1; n <= 34; n++)
        {
            body.Add((byte)(n <= 33 ? 31 + n : 71));
            uint v = values.TryGetValue(n, out uint got) ? got : 0u;
            body.Add((byte)(v >> 24));
            body.Add((byte)(v >> 16));
            body.Add((byte)(v >> 8));
            body.Add((byte)v);
        }

        // 头字节：(reserved<<6)|(dtraitType<<5)|(accessType<<4)|version = 0x20
        var blob = new List<byte> { 0x20 };
        blob.AddRange(BoolBuffer(Bools));
        blob.AddRange(body);

        _cachedBlob = Convert.ToBase64String(blob.ToArray());
        _cachedProfileKey = key;
        return _cachedBlob;
    }

    /// <summary>可以从设备字段现算的特征。</summary>
    private static Dictionary<int, string> ComputedFeatures(
        string ua, int screenW, int screenH, int cores, int memory)
    {
        string J(double x) => JsNumberStr(x);

        double tan = Math.Tan(-1e300);
        double atanh = Math.Atanh(0.5);
        double atanhPf = Math.Log((1 + 0.5) / (1 - 0.5)) / 2;
        double cos = Math.Cos(10.000000000123);
        double sin = Math.Sin(-1e300);
        double powPi = Math.Pow(Math.PI, -100);

        return new Dictionary<int, string>
        {
            [11] = $"{J(tan)},{J(atanh)},{J(atanhPf)},{J(cos)}",
            [12] = $"{J(1.7182818284590453)},{J(powPi)},{J(sin)},{J(tan)}",
            [14] = "10,4g",
            [15] = "zh-CN,zh-CN,zh,en,zh-TW,ja",
            [16] = ",Google Inc.",
            [17] = "Win32,undefined",
            [18] = "",
            [19] = ua,
            [27] = "zh-CN+Asia/Shanghai",
            [28] = "default",
            [29] = $"10,{memory},{cores},10",
            [30] = $"{screenH - 48},0,0,{screenW}",
            [31] = $"{screenH},{screenW}",
            [32] = "24,24,1",
        };
    }

    /// <summary>VM getBoolBuffer 的等价实现。</summary>
    private static byte[] BoolBuffer(Dictionary<int, bool> bools)
    {
        if (bools.Count == 0) return Array.Empty<byte>();

        int maxIdx = bools.Keys.Max();
        int size = (maxIdx / 32 + 1) * 5;
        var buf = new byte[size];

        foreach (int n in bools.Keys.OrderBy(x => x))
        {
            if (n % 32 == 0) buf[n / 32] = (byte)(n / 8);
            if (!bools[n]) continue;

            int idx = 5 * (n / 32 + 1) - (n % 32) / 8 - 1;
            buf[idx] |= (byte)(1 << (n % 8));
        }

        return buf;
    }

    /// <summary>MurmurHash3 x86 32-bit。</summary>
    public static uint Murmur3_32(string text, uint seed = 0)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        const uint c1 = 0xCC9E2D51, c2 = 0x1B873593;
        uint h = seed;

        int n = data.Length / 4 * 4;
        for (int i = 0; i < n; i += 4)
        {
            uint k = (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
            k *= c1;
            k = (k << 15) | (k >> 17);
            k *= c2;
            h ^= k;
            h = (h << 13) | (h >> 19);
            h = h * 5 + 0xE6546B64;
        }

        uint tail = 0;
        int rest = data.Length - n;
        for (int i = 0; i < rest; i++)
            tail |= (uint)(data[n + i] << (8 * i));

        if (rest > 0)
        {
            tail *= c1;
            tail = (tail << 15) | (tail >> 17);
            tail *= c2;
            h ^= tail;
        }

        h ^= (uint)data.Length;
        h ^= h >> 16;
        h *= 0x85EBCA6B;
        h ^= h >> 13;
        h *= 0xC2B2AE35;
        h ^= h >> 16;
        return h;
    }

    /// <summary>
    /// 等价于 JS 的 Number.prototype.toString()。
    /// 数学指纹要拼成字符串再算 murmur3，格式和 C# 的 ToString 不一样。
    /// </summary>
    public static string JsNumberStr(double x)
    {
        if (double.IsNaN(x)) return "NaN";
        if (double.IsPositiveInfinity(x)) return "Infinity";
        if (double.IsNegativeInfinity(x)) return "-Infinity";
        if (x == 0) return "0";

        string sign = x < 0 ? "-" : "";
        string r = Math.Abs(x).ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        string digits;
        int n;
        int ePos = r.IndexOfAny(new[] { 'E', 'e' });
        if (ePos >= 0)
        {
            string mant = r[..ePos];
            int exp = int.Parse(r[(ePos + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            int dot = mant.IndexOf('.');
            digits = mant.Replace(".", "").TrimEnd('0');
            if (digits.Length == 0) digits = "0";
            n = exp + (dot < 0 ? mant.Length : dot);
        }
        else if (r.Contains('.'))
        {
            string[] parts = r.Split('.');
            if (parts[0] == "0")
            {
                string stripped = parts[1].TrimStart('0');
                n = -(parts[1].Length - stripped.Length);
                digits = stripped.TrimEnd('0');
                if (digits.Length == 0) digits = "0";
            }
            else
            {
                digits = (parts[0] + parts[1]).TrimEnd('0');
                if (digits.Length == 0) digits = "0";
                n = parts[0].Length;
            }
        }
        else
        {
            digits = r.TrimEnd('0');
            if (digits.Length == 0) digits = "0";
            n = r.Length;
        }

        int k = digits.Length;
        string s;
        if (k <= n && n <= 21)
            s = digits + new string('0', n - k);
        else if (0 < n && n <= 21)
            s = digits[..n] + "." + digits[n..];
        else if (-6 < n && n <= 0)
            s = "0." + new string('0', -n) + digits;
        else
        {
            int e = n - 1;
            s = digits[..1] + (k > 1 ? "." + digits[1..] : "");
            s += "e" + (e >= 0 ? "+" : "-") + Math.Abs(e);
        }

        return sign + s;
    }
}
