using System.Text;
using System.Text.Json;
using DouyinDesktop.Core.Signing;

// 端到端验证：不依赖任何硬编码凭据，自己注册 ttwid、自己生成 s_v_web_id、
// 自己从响应头捡 msToken，然后请求真实推荐流。
// 这是"能不能独立跑起来"的判定标准。

const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                  "(KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36";

using var http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = true });
http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Ua);

// 1) 注册匿名设备标识
string ttwid = await RegisterTtwidAsync(http);
Console.WriteLine($"[1] ttwid = {(ttwid.Length > 40 ? ttwid[..40] + "..." : ttwid)}  长度 {ttwid.Length}");
if (ttwid.Length == 0)
{
    Console.WriteLine("    ttwid 注册失败，后面大概率会被风控");
}

// 2) 生成设备指纹
string svWebId = GenerateSvWebId();
Console.WriteLine($"[2] s_v_web_id = {svWebId}");

// 3) 请求推荐流；第一次 msToken 为空，服务端可能在响应头里下发一个
string msToken = "";
int cards = 0;

for (int attempt = 1; attempt <= 3; attempt++)
{
    var result = await FetchFeedAsync(http, ttwid, svWebId, msToken, attempt);
    cards = result.Cards;

    if (cards > 0)
    {
        Console.WriteLine($"\n=== 第 {attempt} 次成功：{cards} 条 ===  总耗时 {result.ElapsedMs}ms");
        foreach (var item in result.Items.Take(3))
            Console.WriteLine($"  {item.Id}  uri={item.Uri}  {Truncate(item.Desc, 28)}");

        // 顺带验证播放地址能不能换出来
        if (result.Items.Count > 0)
        {
            string playUrl = BuildPlayUrl(result.Items[0].Uri, "1080p");
            Console.WriteLine($"\n[4] 播放地址（302 入口）:\n    {playUrl}");
            using var head = new HttpRequestMessage(HttpMethod.Get, playUrl);
            head.Headers.TryAddWithoutValidation("Referer", $"https://www.douyin.com/video/{result.Items[0].Id}");
            head.Headers.TryAddWithoutValidation("Range", "bytes=0-1023");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var pr = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead);
            sw.Stop();
            Console.WriteLine($"    最终状态 {(int)pr.StatusCode}  类型 {pr.Content.Headers.ContentType}  " +
                              $"耗时 {sw.ElapsedMilliseconds}ms  重定向 {pr.RequestMessage?.RequestUri?.Host}");
        }
        return 0;
    }

    Console.WriteLine($"\n[3] 第 {attempt} 次：cards=0（HTTP {result.Status}，{result.BodyLength} 字节）");
    if (!string.IsNullOrEmpty(result.NewMsToken))
    {
        msToken = result.NewMsToken;
        Console.WriteLine($"    拿到服务端下发的 msToken（{msToken.Length} 字符），重试");
    }
    else
    {
        Console.WriteLine("    响应头里没有 X-Ms-Token，无法自动续期");
        if (result.BodyLength > 0 && result.BodySnippet.Length > 0)
            Console.WriteLine("    响应片段：" + Truncate(result.BodySnippet, 200));
        break;
    }
}

Console.WriteLine($"\n未拿到推荐流（cards={cards}）");
return 1;

// ================= 请求逻辑 =================

static async Task<(int Cards, List<(string Id, string Uri, string Desc)> Items, int ElapsedMs,
                   int Status, int BodyLength, string BodySnippet, string NewMsToken)>
    FetchFeedAsync(HttpClient http, string ttwid, string svWebId, string msToken, int attempt)
{
    var ps = new (string Key, string Value)[]
    {
        ("device_platform", "webapp"), ("aid", "6383"), ("channel", "channel_pc_web"),
        ("module_id", "3003101"), ("count", "10"), ("filterGids", ""), ("presented_ids", ""),
        ("refresh_index", "2"), ("refer_id", ""), ("refer_type", "10"),
        ("awemePcRecRawData", "{\"is_client\":false}"), ("Seo-Flag", "0"),
        ("install_time", "1715480185"), ("pc_client_type", "1"),
        ("update_version_code", "170400"), ("version_code", "170400"), ("version_name", "17.4.0"),
        ("cookie_enabled", "true"), ("screen_width", "2560"), ("screen_height", "1440"),
        ("browser_language", "zh-CN"), ("browser_platform", "Win32"),
        ("browser_name", "Chrome"), ("browser_version", "151.0.0.0"), ("browser_online", "true"),
        ("engine_name", "Blink"), ("engine_version", "151.0.0.0"),
        ("os_name", "Windows"), ("os_version", "10"),
        ("cpu_core_num", "20"), ("device_memory", "32"),
        ("platform", "PC"), ("downlink", "10"), ("effective_type", "4g"), ("round_trip_time", "100"),
        ("webid", GenerateFakeWebId()), ("msToken", msToken),
    };

    string query = string.Join("&", ps.Select(p => p.Key + "=" + Quote(p.Value)));
    var signer = new ABogus(@fixed: false, userAgent: Ua);
    string abogus = signer.SignQuery(query, "", "www.douyin.com");

    string url = "https://www.douyin.com/aweme/v1/web/module/feed/?" + query
               + "&a_bogus=" + Quote(abogus)
               + "&verifyFp=" + svWebId + "&fp=" + svWebId;

    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.TryAddWithoutValidation("Referer", "https://www.douyin.com/");
    req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
    req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
    req.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Not=A?Brand\";v=\"99\", \"Google Chrome\";v=\"151\", \"Chromium\";v=\"151\"");
    req.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
    req.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
    req.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
    req.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
    req.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
    req.Headers.TryAddWithoutValidation("priority", "u=1, i");

    var cookieParts = new List<string> { $"s_v_web_id={svWebId}" };
    if (!string.IsNullOrEmpty(ttwid)) cookieParts.Add($"ttwid={ttwid}");
    req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookieParts));

    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var resp = await http.SendAsync(req);
    string body = await resp.Content.ReadAsStringAsync();
    sw.Stop();

    string newMsToken = resp.Headers.TryGetValues("X-Ms-Token", out var mt) ? mt.FirstOrDefault() ?? "" : "";

    var items = new List<(string, string, string)>();
    int cards = 0;

    if (body.TrimStart().StartsWith('{'))
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("cards", out var c) && c.ValueKind == JsonValueKind.Array)
            {
                foreach (var card in c.EnumerateArray())
                {
                    if (!card.TryGetProperty("aweme", out var aw)) continue;
                    cards++;
                    using var inner = aw.ValueKind == JsonValueKind.String
                        ? JsonDocument.Parse(aw.GetString()!)
                        : JsonDocument.Parse(aw.GetRawText());
                    var a = inner.RootElement;
                    string id = a.TryGetProperty("aweme_id", out var i) ? i.GetString() ?? "" : "";
                    string desc = a.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "";
                    string uri = "";
                    if (a.TryGetProperty("video", out var v) && v.TryGetProperty("play_addr", out var pa)
                        && pa.TryGetProperty("uri", out var u))
                        uri = u.GetString() ?? "";
                    items.Add((id, uri, desc));
                }
            }
        }
        catch (JsonException) { /* 当作失败处理 */ }
    }

    return (cards, items, (int)sw.ElapsedMilliseconds, (int)resp.StatusCode, body.Length,
            Truncate(body, 200), newMsToken);
}

// 网页播放器自己走的入口：用 uri 换真实流，服务端 302 到 CDN
static string BuildPlayUrl(string uri, string ratio)
    => $"https://www.douyin.com/aweme/v1/play/?video_id={uri}&ratio={ratio}&line=0";

// ================= 凭据获取 =================

static async Task<string> RegisterTtwidAsync(HttpClient http)
{
    var body = new
    {
        region = "cn", aid = 1768, needFid = false, service = "www.douyin.com",
        migrate_info = new { ticket = "", source = "node" },
        cbUrlProtocol = "https", union = true,
    };

    using var req = new HttpRequestMessage(HttpMethod.Post, "https://ttwid.bytedance.com/ttwid/union/register/");
    req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    req.Headers.TryAddWithoutValidation("User-Agent", Ua);

    try
    {
        using var resp = await http.SendAsync(req);
        if (resp.Headers.TryGetValues("Set-Cookie", out var vals))
        {
            foreach (string v in vals)
            {
                int idx = v.IndexOf("ttwid=", StringComparison.Ordinal);
                if (idx < 0) continue;
                string tail = v[(idx + 6)..];
                int semi = tail.IndexOf(';');
                return semi > 0 ? tail[..semi] : tail;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("    ttwid 请求异常：" + ex.Message);
    }
    return "";
}

// ================= 工具 =================

// 等价于 Python 的 urllib.parse.quote(s, safe="")
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
    return $"verify_{ts36}_{Rnd(8)}_{Rnd(4)}_4{Rnd(3)}_{"89ab"[Random.Shared.Next(4)]}{Rnd(3)}_{Rnd(12)}";
}

static string GenerateFakeWebId()
    => string.Concat(Enumerable.Range(0, 19).Select(_ => (char)('0' + Random.Shared.Next(10))));

static string Truncate(string s, int max)
    => s.Length <= max ? s : s[..max] + "…";
