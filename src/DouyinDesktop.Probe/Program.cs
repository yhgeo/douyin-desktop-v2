using System.Text;
using System.Text.Json;
using DouyinDesktop.Core.Signing;

// 端到端验证：用 C# 算的 a_bogus 请求真实推荐流。
// 目的不是"能跑"，而是确认服务端接受 C# 的签名（golden test 只证明和 Python 算得一样）。

const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                  "(KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36";

string svWebId = GenerateSvWebId();
Console.WriteLine($"s_v_web_id = {svWebId}");

// 参数顺序必须与浏览器抓包一致：签名是对这串字节做摘要的，顺序变了签名就不同
var ps = new (string Key, string Value)[]
{
    ("device_platform", "webapp"),
    ("aid", "6383"),
    ("channel", "channel_pc_web"),
    ("module_id", "3003101"),
    ("count", "10"),
    ("filterGids", ""),
    ("presented_ids", ""),
    ("refresh_index", "2"),
    ("refer_id", ""),
    ("refer_type", "10"),
    ("awemePcRecRawData", "{\"is_client\":false}"),
    ("Seo-Flag", "0"),
    ("install_time", "1715480185"),
    ("pc_client_type", "1"),
    ("update_version_code", "170400"),
    ("version_code", "170400"),
    ("version_name", "17.4.0"),
    ("cookie_enabled", "true"),
    ("screen_width", "2560"),
    ("screen_height", "1440"),
    ("browser_language", "zh-CN"),
    ("browser_platform", "Win32"),
    ("browser_name", "Chrome"),
    ("browser_version", "151.0.0.0"),
    ("browser_online", "true"),
    ("engine_name", "Blink"),
    ("engine_version", "151.0.0.0"),
    ("os_name", "Windows"),
    ("os_version", "10"),
    ("cpu_core_num", "20"),
    ("device_memory", "32"),
    ("platform", "PC"),
    ("downlink", "10"),
    ("effective_type", "4g"),
    ("round_trip_time", "100"),
    ("webid", "7699090210374305299"),
    ("msToken", "2PifiICp6AH_I_ce2yPctWAdBa7NygDWOMYfOgz2sIAHULM8eeucE21SZbUd3-jsl29xFq6o_dGKWb9CNzLs-GQVwc4cwyqo-sz4TFqjA-bbEb39xO1z61Cd6WMO_crt6b4i2WWvlcgoXD2nKLwhcJ_RlyK5z3oKMp0="),
};

string query = string.Join("&", ps.Select(p => p.Key + "=" + Quote(p.Value)));

var signer = new ABogus(@fixed: false, userAgent: Ua);
string abogus = signer.SignQuery(query, "", "www.douyin.com");
Console.WriteLine($"a_bogus  = {abogus}  (长度 {abogus.Length})");

string finalQuery = query
    + "&a_bogus=" + Quote(abogus)
    + "&verifyFp=" + svWebId
    + "&fp=" + svWebId;

string url = "https://www.douyin.com/aweme/v1/web/module/feed/?" + finalQuery;

// 游客态 Cookie：这批是 Python 版 bootstrap 从服务端换来的。
// 先用固定值验证签名是否被接受，通过后再实现自动获取。
var cookies = new Dictionary<string, string>
{
    ["s_v_web_id"] = svWebId,
    ["ttwid"] = "1%7C87hgyhCqN1ftove_5ud38ALMHJgNozKHxk5zmczSgFQ%7C1792584136%7Cb66e319d5b6f14acc88d836d29a8b1ca454a96ec9e95af7c7c2f5fe890531bc6",
    ["__ac_nonce"] = "06ad8a9c700f8f0264103",
    ["__ac_signature"] = "_02B4Z6wo00f019z8XJgAAIDCUbwlCMaf3ivc3FgAAJ3x39",
    ["bd_ticket_guard_client_data"] = "eyJiZC10aWNrZXQtZ3VhcmQtdmVyc2lvbiI6MiwiYmQtdGlja2V0LWd1YXJkLWl0ZXJhdGlvbi12ZXJzaW9uIjoxLCJiZC10aWNrZXQtZ3VhcmQtcmVlX3B1YmxpYy1rZXkiOiJCRDNTU0J2ZjVrZXkwSVJNaWRTeTFmNWJsUEg3MzFmNUtrRi9rTURuUm1sVzZNdDk0cVJtLzB4SkxiMnhvSE0zZXpQRDY4Z2tLRjVpZGh1QjhMQTVwd1E9IiwiYmQtdGlja2V0LWd1YXJkLXdlYi12ZXJzaW9uIjoyfQ%3D%3D",
    ["bd_ticket_guard_client_web_domain"] = "2",
    ["passport_csrf_token"] = "43c069153eb28c69438d68de4a8e4a10",
    ["passport_csrf_token_default"] = "43c069153eb28c69438d68de4a8e4a10",
    ["passport_auth_mix_state"] = "dl4zu6co79tuvfszg9zzykusne9mw5tkfb7jy6wrn5c72ngn",
    ["x-web-secsdk-uid"] = "93859323-0ecb-4db9-a16b-cc576e7bf416",
    ["is_dash_user"] = "1",
    ["hevc_supported"] = "true",
    ["IsDouyinActive"] = "true",
    ["enter_pc_once"] = "1",
    ["is_support_rtm_web_ts"] = "1",
    ["my_rd"] = "2",
    ["home_can_add_dy_2_desktop"] = "%220%22",
    ["strategyABtestKey"] = "%221792584135.529%22",
    ["dy_swidth"] = "2560",
    ["dy_sheight"] = "1440",
    ["device_web_cpu_core"] = "20",
    ["device_web_memory_size"] = "32",
    ["architecture"] = "amd64",
    ["biz_trace_id"] = "2ce55cb5",
    ["fpk1"] = "U2FsdGVkX1/ow0tkXbABmZeCe/rf9LSU/rCXOFEmBgemlV1brPK5LoLzcpUS28X8Kku5NQ2CFXMzdOlfAX+q0Q==",
    ["fpk2"] = "6967ec7261b3cbe6a91d798c6b951c60",
    ["stream_recommend_feed_params"] = "%22%7B%5C%22cookie_enabled%5C%22%3Atrue%2C%5C%22screen_width%5C%22%3A2560%2C%5C%22screen_height%5C%22%3A1440%2C%5C%22browser_online%5C%22%3Atrue%2C%5C%22cpu_core_num%5C%22%3A20%2C%5C%22device_memory%5C%22%3A32%2C%5C%22downlink%5C%22%3A10%2C%5C%22effective_type%5C%22%3A%5C%224g%5C%22%2C%5C%22round_trip_time%5C%22%3A0%7D%22",
};

string cookieHeader = string.Join("; ", cookies.Select(kv => kv.Key + "=" + kv.Value));

using var http = new HttpClient(new HttpClientHandler { UseCookies = false });
http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Ua);
http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.douyin.com/");
http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8,zh-TW;q=0.7,ja;q=0.6");
http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua", "\"Not=A?Brand\";v=\"99\", \"Google Chrome\";v=\"151\", \"Chromium\";v=\"151\"");
http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
http.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-dest", "empty");
http.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-mode", "cors");
http.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-site", "same-origin");
http.DefaultRequestHeaders.TryAddWithoutValidation("priority", "u=1, i");
http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookieHeader);

Console.WriteLine("\n请求中...");
var sw = System.Diagnostics.Stopwatch.StartNew();
try
{
    using var resp = await http.GetAsync(url);
    sw.Stop();
    string body = await resp.Content.ReadAsStringAsync();

    Console.WriteLine($"HTTP {(int)resp.StatusCode}  耗时 {sw.ElapsedMilliseconds}ms  响应 {body.Length} 字符");
    Console.WriteLine("--- 响应头 ---");
    foreach (var h in resp.Headers) Console.WriteLine($"  {h.Key}: {string.Join(", ", h.Value)}");
    foreach (var h in resp.Content.Headers) Console.WriteLine($"  {h.Key}: {string.Join(", ", h.Value)}");

    if (!body.TrimStart().StartsWith('{'))
    {
        Console.WriteLine("非 JSON 响应，前 300 字：");
        Console.WriteLine(body.Length > 300 ? body[..300] : body);
        return 1;
    }

    using var doc = JsonDocument.Parse(body);
    var root = doc.RootElement;
    int statusCode = root.TryGetProperty("status_code", out var sc) ? sc.GetInt32() : -1;
    int cards = root.TryGetProperty("cards", out var c) && c.ValueKind == JsonValueKind.Array ? c.GetArrayLength() : 0;
    int awemeList = root.TryGetProperty("aweme_list", out var al) && al.ValueKind == JsonValueKind.Array ? al.GetArrayLength() : 0;

    Console.WriteLine($"status_code={statusCode}  cards={cards}  aweme_list={awemeList}");

    if (cards > 0)
    {
        Console.WriteLine("\n=== 服务端接受了 C# 签名 ===");
        int i = 0;
        foreach (var card in root.GetProperty("cards").EnumerateArray())
        {
            if (!card.TryGetProperty("aweme", out var aw)) continue;
            using var inner = aw.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(aw.GetString()!)
                : JsonDocument.Parse(aw.GetRawText());
            var a = inner.RootElement;

            string id = a.TryGetProperty("aweme_id", out var idEl) ? idEl.GetString() ?? "?" : "?";
            string desc = a.TryGetProperty("desc", out var d) ? (d.GetString() ?? "") : "";
            string uri = "";
            if (a.TryGetProperty("video", out var v) && v.TryGetProperty("play_addr", out var pa)
                && pa.TryGetProperty("uri", out var uriEl))
                uri = uriEl.GetString() ?? "";

            Console.WriteLine($"  #{i++} {id}  uri={uri}  {Truncate(desc, 30)}");
            if (i >= 3) break;
        }
        return 0;
    }

    Console.WriteLine("\n没有 cards，响应片段：");
    Console.WriteLine(body.Length > 800 ? body[..800] : body);
    return 1;
}
catch (Exception ex)
{
    Console.WriteLine($"请求失败：{ex.GetType().Name}: {ex.Message}");
    return 1;
}

// ---- 工具 ----

// 等价于 Python 的 urllib.parse.quote(s, safe="")：只保留字母数字和 _.-~
static string Quote(string s)
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

// verify_<base36 时间戳>_<8>_<4>_4xx_<8|9|a|b>xxx_<12>
static string GenerateSvWebId()
{
    const string charset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    long n = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var ts36 = new StringBuilder();
    while (n > 0)
    {
        ts36.Insert(0, "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(n % 36)]);
        n /= 36;
    }

    string Rnd(int k) => string.Concat(Enumerable.Range(0, k).Select(_ => charset[Random.Shared.Next(charset.Length)]));

    string groups = $"{Rnd(8)}_{Rnd(4)}_4{Rnd(3)}_{"89ab"[Random.Shared.Next(4)]}{Rnd(3)}_{Rnd(12)}";
    return $"verify_{ts36}_{groups}";
}

static string GenerateFakeWebId()
    => string.Concat(Enumerable.Range(0, 19).Select(_ => (char)('0' + Random.Shared.Next(10))));

static string Truncate(string s, int max)
    => s.Length <= max ? s : s[..max] + "…";
