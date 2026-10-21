using System.Text;
using System.Text.RegularExpressions;

namespace DouyinDesktop.Core.Signing;

/// <summary>随机数来源。固定模式用于测试，真实模式用于线上请求。</summary>
public interface IRandomSource
{
    double Next();
}

/// <summary>永远返回同一个值。用于 golden test 复现签名。</summary>
public sealed class FixedRandomSource : IRandomSource
{
    private readonly double _value;
    public FixedRandomSource(double value) => _value = value;
    public double Next() => _value;
}

public sealed class SystemRandomSource : IRandomSource
{
    public double Next() => Random.Shared.NextDouble();
}

/// <summary>
/// a_bogus 签名。
///
/// 移植自 vendor/DouYin_Spider/utils/ab_pure.py（369 行）。
/// 刻意保留了原实现的 L[索引] 写法和 z143/z144/z146/z148 这类命名——
/// 改名字之后就没法和 Python 版逐行对账了，查错时会很痛苦。
///
/// 签名结构：4 字节头（L[89]）+ RC4 加密的载荷，用自定义字母表做 base64。
/// </summary>
public sealed class ABogus
{
    // ---- 常量（与 Python 版逐项对应） ----
    private const string Salt = "dhzx";
    private const int SdkVersionCode = 1;
    private const int SdkMinorCode = 12;
    private const int L40Flag = 0;
    private const int L41Flag = 0;
    private const int L42Flag = 0;

    private const long FortnightEpoch = 1721836800000L;
    private const long DumpClosureTime = 1720000000000L;
    private const int DumpCounterInit = 2;
    private const string DumpBrowserName = "Firefox";
    private const int Rc4KeyByte = 211;

    public const long FixedNow = 1720000000000L;
    public const double FixedRand = 0.4142135623730951;

    private const string AlphabetS3 = "ckdp1h4ZKsUB80/Mfvw36XIgR25+WQAlEi7NLboqYTOPuzmFjJnryx9HVGDaStCe";
    private const string AlphabetS4 = "Dkdpgh2ZmsQB80/MfvV36XI1R45-WUAlEixNLwoqYTOPuzKFjJnry79HbGcaStCe";

    public const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";

    public const string FirefoxUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/117.0";

    private static readonly int[] GeoValues = { 2560, 1215, 2560, 1392, 2560, 1392, 2560, 1440 };
    private const string GeoPlatform = "Win32";

    private static readonly Dictionary<string, (int Aid, int PageId)> HostAppIds = new()
    {
        ["www.douyin.com"] = (6383, 11881),
        ["live.douyin.com"] = (6383, 7571),
        ["creator.douyin.com"] = (2906, 33638),
        ["login.douyin.com"] = (6383, 6241),
    };

    private static readonly Dictionary<string, HostConfig> HostConfigs = new()
    {
        ["www.douyin.com"] = new HostConfig { SdkMinor = 8, L40 = 132, L41 = 1, L42 = 1 },
        ["login.douyin.com"] = new HostConfig { SdkMinor = 14, L40 = 0, L41 = 0, L42 = 0 },
    };

    private const string DefaultHost = "www.douyin.com";

    private static readonly int[] A98Perm =
    {
        34, 44, 56, 61, 73, 29, 70, 45, 35, 49, 38, 66, 51, 68, 28, 48, 64, 47,
        30, 71, 26, 55, 31, 69, 59, 40, 62, 63, 27, 72, 41, 74, 57, 52, 42, 39,
        33, 67, 53, 43, 65, 46, 36, 24, 60, 32, 79, 80, 84, 85,
    };

    private static readonly int[] Z148RMasks = { 145, 66, 44 };
    private static readonly int[] Z148InMasks = { 110, 189, 211 };
    private const int Z146MaskA = 170;
    private const int Z146MaskB = 85;

    private static readonly (string Name, string[] Patterns)[] BrowserTable =
    {
        ("Huawei", new[] { @"\bhuawei\b" }),
        ("Chrome", new[] { @"(chrome)\/([\w.]+)(?!.*chromium)" }),
        ("Edge",   new[] { @"(edg|edge)\/([\w.]+)" }),
        ("Firefox", new[]
        {
            @"\bfocus\/([\w.]+)", @"fxios\/([-\w.]+)",
            @"mobile vr; rv:([\w.]+)\).+firefox", @"(firefox)\/([\w.]+)",
        }),
        ("IE",    new[] { @"(msie |trident.*rv:)([\w.]+)" }),
        ("Opera", new[] { @"(opera|opr)\/([\w.]+)" }),
        ("Safari", new[] { @"(safari)\/([\w.]+)(?!.*chrome)" }),
    };

    private static readonly Dictionary<string, int> BrowserOffset = new()
    {
        ["Chrome"] = 0, ["Firefox"] = 40, ["Safari"] = 81, ["Edge"] = 125, ["Huawei"] = 170,
    };

    // ---- 实例状态 ----
    private readonly bool _fixed;
    private readonly string _ua;
    private readonly IRandomSource _rand;
    private readonly long? _fixedNowMs;
    private readonly int[] _geo;
    private readonly string _offsetName;
    private int _counter;

    public ABogus(
        bool @fixed = true,
        string? userAgent = null,
        long? fixedNowMs = null,
        int? fixedCounter = null,
        IRandomSource? random = null)
    {
        _fixed = @fixed;
        _ua = userAgent ?? (@fixed ? FirefoxUa : ChromeUa);
        _fixedNowMs = fixedNowMs;
        _counter = fixedCounter ?? DumpCounterInit;
        _rand = random ?? (@fixed ? new FixedRandomSource(FixedRand) : new SystemRandomSource());
        _geo = GeoValues;
        _offsetName = @fixed ? DumpBrowserName : BrowserName(_ua);
    }

    /// <summary>当前计数器（每次签名自增，参与 L[27] 分桶）。</summary>
    public int Counter => _counter;

    /// <summary>对完整 URL 签名，只取其中的 query 部分。</summary>
    public string Sign(string url, string body = "", string? host = null)
    {
        var uri = new Uri(url);
        string query = uri.Query.StartsWith('?') ? uri.Query[1..] : uri.Query;
        return SignQuery(query, body, host ?? uri.Host);
    }

    /// <summary>
    /// 对 query 串签名。host 决定内嵌的 (aid, page_id)，各子域不同，
    /// 用错会被判人机验证。
    /// </summary>
    public string SignQuery(string query, string body = "", string? host = null)
    {
        host ??= DefaultHost;
        var (aid, pageId) = AppIdsFor(host);
        var hostConfig = HostConfigFor(host);

        int[] L = new int[90];

        _counter += 1;
        long t = Now();
        L[12] = 3;
        L[14] = unchecked((int)t);

        int v129 = hostConfig.SdkVersion;
        int v14 = hostConfig.SdkMinor;

        // 双重 SM3：先 (query + SALT) 摘要两次
        byte[] h1 = Sm3.Hash(Sm3.Hash(query + Salt));
        byte[] h2 = Sm3.Hash(Sm3.Hash(body + Salt));

        // UA 走 RC4，密钥由两个版本号拼成，再 base64(S3) 后取摘要
        int[] uaKey = { v129 / 256, v129 % 256, v14 % 256 };
        int[] uaCipher = Rc4Variant(uaKey, _ua.Trim().Select(c => (int)c).ToArray());
        byte[] hUa = Sm3.Hash(B64Custom(uaCipher, AlphabetS3));

        long ink = t - 1;
        L[23] = 3;  // 原实现是 [3, 82]，数组本身未被引用，只用到这两个值
        L[24] = 41;
        L[25] = 1;  // 原实现是 [1,0,1,0,1]，仅前四项参与 blend，见下方 A8
        L[26] = (int)((t - FortnightEpoch) / 1000.0 / 60 / 60 / 24 / 14);
        L[27] = Z149CounterBucket(_counter);

        long closureT = _fixed ? DumpClosureTime : t;
        L[28] = closureT > 0 ? (int)((t - closureT + 3) & 255) : 2;

        WriteLe(L, 29, t, 6);
        WriteLe(L, 35, v129, 2);

        int flags = EnvFlagsByte(_fixed, _ua);
        L[37] = 0;  // 原实现是 [0,0,0,0,flags]，数组本身未被引用
        WriteLe(L, 38, flags, 2);
        L[40] = hostConfig.L40;
        L[41] = hostConfig.L41;
        L[42] = hostConfig.L42;
        L[43] = 0;
        WriteLe(L, 44, v14, 4);

        L[48] = h1[9];
        L[49] = h1[18];
        L[51] = EscapeDigest(h1, 3, 11, 12, (flags & 2) != 0);
        L[52] = h2[10];
        L[53] = h2[19];
        L[55] = EscapeDigest(h2, 4, 8, 9, (flags & 4) != 0);
        L[56] = hUa[11];
        L[57] = hUa[21];
        L[59] = EscapeDigest(hUa, 5, 12, 13, (flags & 8) != 0);

        WriteLe(L, 60, ink, 6);
        L[66] = L[12];
        WriteLe(L, 67, pageId, 4);
        WriteLe(L, 71, aid, 4);

        // 屏幕尺寸串："2560|1215|...|Win32"
        int[] l77 = StrToBytes(string.Join("|", _geo) + "|" + GeoPlatform);
        L[77] = l77.Length;
        L[78] = l77.Length;
        WriteLe(L, 79, l77.Length, 2);

        // 时间尾串："3,"
        int[] l82 = StrToBytes(((t + 3) & 255) + ",");
        L[82] = l82.Length;
        L[83] = l82.Length;
        WriteLe(L, 84, l82.Length, 2);

        // ---- A8：8 字节校验区 ----
        int v6 = (int)(_rand.Next() * 65535);
        int[] a8 = Z146Blend(L[25], 0, v6 & 255, (v6 >> 8) & 255);
        _rand.Next(); // 原实现这里丢弃一次随机数，顺序必须保留
        a8 = a8.Concat(Z146Blend(1, 0, Z144Check(_rand, flags), Z145PermissionsBits(_rand))).ToArray();
        L[86] = a8.Length;

        // ---- 异或校验 ----
        int[] chkIndexes =
        {
            24, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 38, 39, 40, 41, 42, 43,
            44, 45, 46, 47, 48, 49, 51, 52, 53, 55, 56, 57, 59, 60, 61, 62, 63, 64,
            65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 79, 80, 84, 85,
        };
        int chk = 0;
        foreach (int b in a8) chk ^= b;
        foreach (int idx in chkIndexes) chk ^= L[idx];
        L[87] = ToInt32(chk);

        // ---- A98：按置换表取 50 个字节，再拼屏幕串、时间串、校验和 ----
        int[] a98 = new int[A98Perm.Length + l77.Length + l82.Length + 1];
        int p = 0;
        foreach (int idx in A98Perm) a98[p++] = L[idx];
        Array.Copy(l77, 0, a98, p, l77.Length); p += l77.Length;
        Array.Copy(l82, 0, a98, p, l82.Length); p += l82.Length;
        a98[p] = L[87];
        L[88] = a98.Length;

        // ---- 头 4 字节 ----
        int hr0 = (int)(_rand.Next() * 65535) & 255;
        int[] l89 = Z146Blend(3, 82, hr0, Z143RandOffset(_rand, _offsetName));

        // ---- 载荷：A8 + 扩展后的 A98，整体 RC4 ----
        int[] plain = a8.Concat(Z148Expand(a98, _rand)).ToArray();
        int[] cipher = Rc4Variant(new[] { Rc4KeyByte }, plain.Select(x => x & 0xFFFF).ToArray());

        int[] output = new int[l89.Length + cipher.Length];
        Array.Copy(l89, 0, output, 0, l89.Length);
        for (int i = 0; i < cipher.Length; i++) output[l89.Length + i] = cipher[i] & 255;

        return B64Custom(output, AlphabetS4);
    }

    // ================= 工具函数 =================

    private long Now()
    {
        if (_fixedNowMs.HasValue) return _fixedNowMs.Value;
        return _fixed ? FixedNow : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static (int Aid, int PageId) AppIdsFor(string host)
        => HostAppIds.TryGetValue(host, out var v) ? v : HostAppIds[DefaultHost];

    private static HostConfig HostConfigFor(string host)
        => HostConfigs.TryGetValue(host, out var c)
            ? c
            : new HostConfig { SdkVersion = SdkVersionCode, SdkMinor = SdkMinorCode, L40 = L40Flag, L41 = L41Flag, L42 = L42Flag };

    private static void WriteLe(int[] target, int offset, long value, int count)
    {
        for (int i = 0; i < count; i++) target[offset + i] = (int)((value >> (8 * i)) & 255);
    }

    private static int[] LeBytes(long value, int count)
    {
        int[] r = new int[count];
        for (int i = 0; i < count; i++) r[i] = (int)((value >> (8 * i)) & 255);
        return r;
    }

    /// <summary>Python 的 ord() 语义：大于 255 的码点拆成高低两字节。</summary>
    private static int[] StrToBytes(string s)
    {
        var outList = new List<int>(s.Length);
        foreach (char ch in s)
        {
            int c = ch;
            if ((c & 65280) != 0)
            {
                outList.Add(c >> 8);
                outList.Add(c & 255);
            }
            else
            {
                outList.Add(c);
            }
        }
        return outList.ToArray();
    }

    /// <summary>
    /// Python 的 to_int32：把 32 位无符号值折成有符号。
    /// C# 的 int 本身就是 32 位有符号、溢出自动回绕，所以是恒等操作。
    /// 保留这个函数是为了和参考实现逐行对得上。
    /// </summary>
    private static int ToInt32(int x) => x;

    private static int[] Z146Blend(int c0, int c1, int r0, int r1) => new[]
    {
        (r0 & Z146MaskA) | (c0 & Z146MaskB),
        (r0 & Z146MaskB) | (c0 & Z146MaskA),
        (r1 & Z146MaskA) | (c1 & Z146MaskB),
        (r1 & Z146MaskB) | (c1 & Z146MaskA),
    };

    /// <summary>每 3 字节扩展成 4 字节，每轮消耗一个随机数。</summary>
    private static int[] Z148Expand(int[] arr, IRandomSource rand)
    {
        var output = new List<int>(arr.Length * 4 / 3 + 2);
        int i = 0;
        int n = arr.Length;
        while (i < n)
        {
            if (i + 2 < n)
            {
                int r = (int)(rand.Next() * 1000) & 255;
                output.Add((r & Z148RMasks[0]) | (arr[i] & Z148InMasks[0]));
                output.Add((r & Z148RMasks[1]) | (arr[i + 1] & Z148InMasks[1]));
                output.Add((r & Z148RMasks[2]) | (arr[i + 2] & Z148InMasks[2]));
                output.Add((arr[i] & Z148RMasks[0]) | (arr[i + 1] & Z148RMasks[1]) | (arr[i + 2] & Z148RMasks[2]));
            }
            else
            {
                output.Add(arr[i]);
                if (i + 1 < n && arr[i + 1] != 0) output.Add(arr[i + 1]);
            }
            i += 3;
        }
        return output.ToArray();
    }

    /// <summary>避开保留值：命中就往后找，找不到用 fallback。</summary>
    private static int EscapeDigest(byte[] digest, int idx, int reserved, int fallback, bool force)
    {
        int v = idx < digest.Length ? digest[idx] : fallback;
        while (v == reserved)
        {
            idx++;
            v = idx < digest.Length ? digest[idx] : fallback;
        }
        return force ? reserved : v;
    }

    private static string BrowserName(string ua)
    {
        foreach (var (name, patterns) in BrowserTable)
        {
            foreach (string p in patterns)
            {
                if (Regex.IsMatch(ua, p, RegexOptions.IgnoreCase)) return name;
            }
        }
        return "Other";
    }

    private static int Z143RandOffset(IRandomSource rand, string name)
        => (int)(rand.Next() * 40) + (BrowserOffset.TryGetValue(name, out int v) ? v : 210);

    private static int Z144Check(IRandomSource rand, int flagsByte)
    {
        if ((flagsByte & 64) != 0)
        {
            int r = (int)(rand.Next() * 109);
            return r + 110 + (r % 2);
        }
        int r2 = (int)(rand.Next() * 240);
        return r2 > 109 ? r2 + (r2 % 2) + 1 : r2;
    }

    private static int Z145PermissionsBits(IRandomSource rand)
    {
        int baseValue = (int)(rand.Next() * 255) & 77;
        return baseValue | 2 | 16 | 32 | 128;
    }

    /// <summary>计数器分桶。注意调用方传入的 counter 已经自增过。</summary>
    private static int Z149CounterBucket(int counter)
    {
        if (counter > 10745) return 3;
        if (counter > 1283) return 4;
        if (counter > 139) return 5;
        return 6;
    }

    /// <summary>
    /// 环境标志位。
    /// bit1 在旧版实现里被无条件置位，但 2026-08-16 用页面 SDK 对同一 query
    /// 生成的真值里 flags=1、bit1 是 0（置位会让 L[51] 被强制成保留值 11）。
    /// </summary>
    private static int EnvFlagsByte(bool stackIsNode, string ua)
    {
        int flags = 1;
        flags |= (stackIsNode ? 1 : 0) << 2;
        flags |= (BrowserName(ua) == "Firefox" ? 1 : 0) << 5;
        return flags;
    }

    /// <summary>标准 base64 的变体：字母表可换，尾部补 '='。</summary>
    private static string B64Custom(IReadOnlyList<int> data, string alphabet)
    {
        var sb = new StringBuilder((data.Count + 2) / 3 * 4);
        for (int i = 0; i < data.Count; i += 3)
        {
            int n = Math.Min(3, data.Count - i);
            int b0 = data[i];
            int b1 = n > 1 ? data[i + 1] : 0;
            int b2 = n > 2 ? data[i + 2] : 0;
            int v = (b0 << 16) | (b1 << 8) | b2;

            sb.Append(alphabet[(v >> 18) & 63]);
            sb.Append(alphabet[(v >> 12) & 63]);
            sb.Append(n > 1 ? alphabet[(v >> 6) & 63] : '=');
            sb.Append(n > 2 ? alphabet[v & 63] : '=');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 变体 RC4：S 盒初始化为 255-i（不是 0..255），KSA 的 j 递推也不同。
    /// 名字里带 "variant" 就是提醒别拿标准 RC4 替换。
    /// </summary>
    private static int[] Rc4Variant(int[] key, int[] data)
    {
        int[] sbox = new int[256];
        for (int i = 0; i < 256; i++) sbox[i] = 255 - i;

        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j * sbox[i] + j + key[i % key.Length]) % 256;
            (sbox[i], sbox[j]) = (sbox[j], sbox[i]);
        }

        int[] output = new int[data.Length];
        int a = 0, b = 0;
        for (int k = 0; k < data.Length; k++)
        {
            a = (a + 1) % 256;
            b = (b + sbox[a]) % 256;
            (sbox[a], sbox[b]) = (sbox[b], sbox[a]);
            output[k] = data[k] ^ sbox[(sbox[a] + sbox[b]) % 256];
        }
        return output;
    }

    private sealed class HostConfig
    {
        public int SdkVersion { get; init; } = SdkVersionCode;
        public int SdkMinor { get; init; } = SdkMinorCode;
        public int L40 { get; init; } = L40Flag;
        public int L41 { get; init; } = L41Flag;
        public int L42 { get; init; } = L42Flag;
    }
}
