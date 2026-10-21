using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DouyinDesktop.Core.Models;
using DouyinDesktop.Core.Signing;

namespace DouyinDesktop.Core.Api;

public sealed record FeedResult(
    IReadOnlyList<Aweme> Items, bool Success, string Message, long ElapsedMs)
{
    public static FeedResult Fail(string message, long elapsedMs = 0)
        => new(Array.Empty<Aweme>(), false, message, elapsedMs);
}

public sealed record StreamProbe(
    bool Ok, int StatusCode, string ContentType, long ElapsedMs, string FinalHost, string Detail);

/// <summary>
/// 抖音 Web 接口客户端。游客态，不需要登录。
///
/// 关键约定（都是实测出来的，别随手改）：
///   · play_addr.url_list 是加密 hex，**不要解密**，只用 play_addr.uri 换播放入口
///   · 推荐流的 aweme_list 是空的，真实数据在 cards[].aweme，且是 JSON 字符串要二次解析
///   · msToken 可以是空串，服务端不校验；能从 X-Ms-Token 响应头捡到就捡
///   · 请求头 Referer 必须是 www.douyin.com，播放地址同理
/// </summary>
public sealed class DouyinClient : IDisposable
{
    private const string FeedEndpoint = "https://www.douyin.com/aweme/v1/web/module/feed/";
    private const string PlayEndpoint = "https://www.douyin.com/aweme/v1/play/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly DeviceProfile _profile;
    private readonly ABogus _signer;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    private DouyinSession? _session;
    private int _refreshIndex = 1;

    /// <summary>诊断用：每次请求的简要记录（失败时界面能显示原因）。</summary>
    public event Action<string>? Log;

    public DeviceProfile Profile => _profile;
    public DouyinSession? Session => _session;

    private DouyinClient(HttpClient http, DeviceProfile profile)
    {
        _http = http;
        _profile = profile;
        _signer = new ABogus(@fixed: false, userAgent: profile.UserAgent);
    }

    public static DouyinClient Create(DeviceProfile? profile = null)
    {
        profile ??= new DeviceProfile();
        var handler = new HttpClientHandler
        {
            UseCookies = false,      // Cookie 头手动拼，便于和签名输入保持一致
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", profile.UserAgent);
        return new DouyinClient(http, profile);
    }

    // ================= 凭据 =================

    /// <summary>确保有可用凭据：先读磁盘缓存，没有再走网络注册。</summary>
    public async Task<DouyinSession> EnsureSessionAsync(
        bool forceRefresh = false, CancellationToken ct = default)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _session is { IsComplete: true, IsStale: false })
                return _session;

            if (!forceRefresh)
            {
                var cached = DouyinSession.Load();
                if (cached is not null)
                {
                    _session = cached;
                    Log?.Invoke($"凭据来自缓存（{cached.CreatedAt:MM-dd HH:mm}）");
                    return cached;
                }
            }

            var sw = Stopwatch.StartNew();
            _session = await DouyinSession.CreateAsync(_http, _profile, ct);
            sw.Stop();
            _session.Save();
            Log?.Invoke(_session.IsComplete
                ? $"凭据已注册（{sw.ElapsedMilliseconds}ms）"
                : $"凭据注册失败（{sw.ElapsedMilliseconds}ms），尝试裸跑");
            return _session;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    // ================= 推荐流 =================

    /// <summary>
    /// 拉一批推荐。presentedIds 传已展示过的 id，让服务端避开重复。
    /// 空结果会自动刷一次凭据重试——多数是风控挡了。
    /// </summary>
    public async Task<FeedResult> GetFeedAsync(
        int count = 10, IEnumerable<string>? presentedIds = null, CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct: ct);

        string presented = presentedIds is null ? "" : string.Join(",", presentedIds);
        var result = await FetchFeedOnceAsync(session, count, presented, ct);
        if (result.Success) return result;

        Log?.Invoke("首次失败，刷新凭据重试：" + result.Message);
        session = await EnsureSessionAsync(forceRefresh: true, ct: ct);
        return await FetchFeedOnceAsync(session, count, presented, ct);
    }

    private async Task<FeedResult> FetchFeedOnceAsync(
        DouyinSession session, int count, string presentedIds, CancellationToken ct)
    {
        int refreshIndex = _refreshIndex++;
        var ps = _profile.FeedParams(count, presentedIds, session.MsToken, refreshIndex);
        string query = UrlCodec.ToQuery(ps);

        // 签名算的是原始 query；a_bogus 和指纹参数追加在签名之后
        string abogus = _signer.SignQuery(query, "", "www.douyin.com");
        string url = $"{FeedEndpoint}?{query}&a_bogus={UrlCodec.Quote(abogus)}" +
                     $"&verifyFp={session.SvWebId}&fp={session.SvWebId}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddBrowserHeaders(req, session);

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            sw.Stop();

            PickUpTokens(resp, session);

            if (!resp.IsSuccessStatusCode)
                return FeedResult.Fail($"HTTP {(int)resp.StatusCode}，{body.Length} 字节", sw.ElapsedMilliseconds);

            var items = ParseFeed(body);
            if (items.Count == 0)
            {
                string snippet = body.Length > 160 ? body[..160].Replace('\n', ' ') + "…" : body;
                return FeedResult.Fail($"响应里没有视频（{body.Length} 字节）：{snippet}", sw.ElapsedMilliseconds);
            }

            Log?.Invoke($"推荐流 {items.Count} 条 / {sw.ElapsedMilliseconds}ms");
            return new FeedResult(items, true, "", sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            sw.Stop();
            return FeedResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private void PickUpTokens(HttpResponseMessage resp, DouyinSession session)
    {
        if (resp.Headers.TryGetValues("X-Ms-Token", out var mt))
        {
            string? token = mt.FirstOrDefault();
            if (!string.IsNullOrEmpty(token) && token != session.MsToken)
            {
                session.MsToken = token;
                session.Save();
            }
        }

        // Set-Cookie 里会下发 UIFID（webSign 签名要用），抓下来存住
        if (resp.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (string c in cookies)
            {
                int idx = c.IndexOf("UIFID=", StringComparison.Ordinal);
                if (idx < 0) continue;

                string tail = c[(idx + 6)..];
                int semi = tail.IndexOf(';');
                string value = semi > 0 ? tail[..semi] : tail;

                if (value.Length > 10 && value != session.Uifid)
                {
                    session.Uifid = value;
                    session.Save();
                    Log?.Invoke($"拿到 UIFID（{value.Length} 字符）");
                }
            }
        }
    }

    /// <summary>
    /// 解析推荐流。结构：cards[].aweme，其中 aweme 是**字符串**（偶尔是对象），
    /// 必须二次解析；没有 aweme 字段的 card 是广告位/提示位，跳过。
    /// </summary>
    public static List<Aweme> ParseFeed(string json)
    {
        var list = new List<Aweme>();
        if (string.IsNullOrWhiteSpace(json) || json.TrimStart()[0] != '{') return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("cards", out var cards)
                || cards.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var card in cards.EnumerateArray())
            {
                if (!card.TryGetProperty("aweme", out var aw)) continue;

                string raw = aw.ValueKind == JsonValueKind.String
                    ? aw.GetString() ?? ""
                    : aw.GetRawText();
                if (raw.Length < 10) continue;

                var item = JsonSerializer.Deserialize<Aweme>(raw, JsonOptions);
                if (item is not null && !string.IsNullOrEmpty(item.Id)) list.Add(item);
            }
        }
        catch (JsonException)
        {
            // 结构变了就返回空，由上层报"没拿到视频"
        }
        return list;
    }

    // ================= 评论 =================

    /// <summary>
    /// 拉一级评论。cursor 传上一页返回的 Cursor，首页传 0。
    /// 登录态下成功率更高（游客态经常只给一部分）。
    /// </summary>
    public async Task<CommentPage> GetCommentsAsync(
        string awemeId, long cursor = 0, int count = 20, CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct: ct);

        var ps = new List<KeyValuePair<string, string>>
        {
            new("device_platform", "webapp"),
            new("aid", "6383"),
            new("channel", "channel_pc_web"),
            new("aweme_id", awemeId),
            new("cursor", cursor.ToString()),
            new("count", count.ToString()),
            new("item_type", "0"),
            // 这两个是空值字段，浏览器确实会发；签名输入里也不能少
            new("whale_cut_token", ""),
            new("cut_version", "1"),
            new("rcFT", ""),
        };
        ps.AddRange(_profile.PlatformParams());
        ps.Add(new("webid", _profile.WebId));
        ps.Add(new("msToken", session.MsToken));

        string query = UrlCodec.ToQuery(ps);
        string abogus = _signer.SignQuery(query, "", "www.douyin.com");
        string url = $"https://www.douyin.com/aweme/v1/web/comment/list/?{query}" +
                     $"&a_bogus={UrlCodec.Quote(abogus)}" +
                     $"&verifyFp={session.SvWebId}&fp={session.SvWebId}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddBrowserHeaders(req, session);
        // 评论接口的 Referer 必须是作品页，首页 Referer 会被挡
        req.Headers.Remove("Referer");
        req.Headers.TryAddWithoutValidation("Referer", $"https://www.douyin.com/video/{awemeId}");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                Log?.Invoke($"评论 HTTP {(int)resp.StatusCode}");
                return CommentPage.Empty;
            }

            var page = ParseComments(body, cursor, count);
            Log?.Invoke($"评论 {page.Comments.Count} 条 / 共 {page.Total}");
            return page;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log?.Invoke("评论请求失败：" + ex.Message);
            return CommentPage.Empty;
        }
    }

    private static CommentPage ParseComments(string json, long cursor, int count)
    {
        if (string.IsNullOrWhiteSpace(json) || json.TrimStart()[0] != '{') return CommentPage.Empty;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("comments", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return CommentPage.Empty;

        var list = new List<Comment>();
        foreach (var c in arr.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            var item = c.Deserialize<Comment>(JsonOptions);
            if (item is not null && !string.IsNullOrEmpty(item.Id)) list.Add(item);
        }

        bool hasMore = root.TryGetProperty("has_more", out var hm)
                       && hm.ValueKind == JsonValueKind.Number && hm.GetInt32() != 0;
        long next = root.TryGetProperty("cursor", out var cur) && cur.ValueKind == JsonValueKind.Number
            ? cur.GetInt64()
            : cursor + count;
        long total = root.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt64()
            : 0;

        return new CommentPage(list, hasMore, next, total);
    }

    // ================= 用户作品 =================

    /// <summary>
    /// 拉某个作者的作品列表。
    ///
    /// 图集作品只能从这里拿到——推荐流（module/feed）实测扫 48 条，
    /// images 字段一条都没有，全是视频。要看图集必须走作者主页。
    /// </summary>
    public async Task<FeedResult> GetUserPostsAsync(
        string secUid, long maxCursor = 0, int count = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(secUid)) return FeedResult.Fail("缺少 sec_uid");

        var session = await EnsureSessionAsync(ct: ct);
        string bv = _profile.BrowserVersion;

        // 参数顺序照 2026-08-16 的抓包（46 项）。
        // 这个接口和推荐流不是一套参数：version_code 是 290100 / 29.1.0，
        // 还多出 publish_video_strategy_type、from_user_page、support_dash 这些。
        var ps = new List<KeyValuePair<string, string>>
        {
            new("device_platform", "webapp"),
            new("aid", "6383"),
            new("channel", "channel_pc_web"),
            new("sec_user_id", secUid),
            new("max_cursor", maxCursor.ToString()),
            new("locate_query", "false"),
            new("show_live_replay_strategy", "1"),
            new("need_time_list", maxCursor == 0 ? "1" : "0"),
            new("time_list_query", "0"),
            new("whale_cut_token", ""),
            new("cut_version", "1"),
            new("count", count.ToString()),
            new("publish_video_strategy_type", "2"),
            new("from_user_page", "1"),   // 0=自己主页，1=他人主页
            new("update_version_code", "170400"),
            new("pc_client_type", "1"),
            new("pc_libra_divert", "Windows"),
            new("support_h265", "1"),
            new("support_dash", "1"),
            new("cpu_core_num", _profile.CpuCores.ToString()),
            new("version_code", "290100"),
            new("version_name", "29.1.0"),
            new("cookie_enabled", "true"),
            new("screen_width", _profile.ScreenWidth.ToString()),
            new("screen_height", _profile.ScreenHeight.ToString()),
            new("browser_language", "zh-CN"),
            new("browser_platform", "Win32"),
            new("browser_name", "Chrome"),
            new("browser_version", bv),
            new("browser_online", "true"),
            new("engine_name", "Blink"),
            new("engine_version", bv),
            new("os_name", "Windows"),
            new("os_version", "10"),
            new("device_memory", _profile.DeviceMemory.ToString()),
            new("platform", "PC"),
            new("downlink", "10"),
            new("effective_type", "4g"),
            new("round_trip_time", "0"),
        };

        string uifid = session.EffectiveUifid;
        ps.Add(new("webid", _profile.WebId));
        if (!string.IsNullOrEmpty(uifid)) ps.Add(new("uifid", uifid));
        ps.Add(new("msToken", session.MsToken));

        string query = UrlCodec.ToQuery(ps);
        string abogus = _signer.SignQuery(query, "", "www.douyin.com");

        // a_bogus 是对不含自己的 query 算的，算完再拼进去；
        // verifyFp/fp 在这个接口里位于 a_bogus 之后（推荐流在之前）
        query += $"&a_bogus={UrlCodec.Quote(abogus)}" +
                 $"&verifyFp={session.SvWebId}&fp={session.SvWebId}";

        // 作者主页在 secsdk 的 webSign 策略表里，整条 query 还要再签一次
        string url = WebSign.SignUrl(
            "https://www.douyin.com/aweme/v1/web/aweme/post/", query, uifid);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddBrowserHeaders(req, session);
        req.Headers.Remove("Referer");
        req.Headers.TryAddWithoutValidation("Referer", $"https://www.douyin.com/user/{secUid}");

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
                return FeedResult.Fail($"HTTP {(int)resp.StatusCode}", sw.ElapsedMilliseconds);

            var items = ParseAwemeList(body);
            if (items.Count == 0)
            {
                string snippet = body.Length > 160 ? body[..160].Replace('\n', ' ') + "…" : body;
                return FeedResult.Fail($"没有作品：{snippet}", sw.ElapsedMilliseconds);
            }

            Log?.Invoke($"用户作品 {items.Count} 条 / {sw.ElapsedMilliseconds}ms");
            return new FeedResult(items, true, "", sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            sw.Stop();
            return FeedResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 用户主页的 aweme_list 是**对象数组**（不像推荐流是 cards[].aweme 字符串），
    /// 直接反序列化就行。数组里可能有 null 占位，跳过。
    /// </summary>
    private static List<Aweme> ParseAwemeList(string json)
    {
        var list = new List<Aweme>();
        if (string.IsNullOrWhiteSpace(json) || json.TrimStart()[0] != '{') return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("aweme_list", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var a in arr.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                var item = a.Deserialize<Aweme>(JsonOptions);
                if (item is not null && !string.IsNullOrEmpty(item.Id)) list.Add(item);
            }
        }
        catch (JsonException) { }
        return list;
    }

    // ================= 播放 =================

    /// <summary>
    /// 播放入口。服务端 302 到 CDN，mpv 自己跟随重定向即可
    /// —— 不在这里解析最终地址，因为 CDN 链接有时效，早解析反而容易过期。
    /// </summary>
    public static string BuildPlayUrl(string videoUri, string ratio = "1080p")
        => $"{PlayEndpoint}?video_id={videoUri}&ratio={ratio}&line=0";

    /// <summary>探针：确认这个 uri 现在真的能拉出 MP4 字节。</summary>
    public async Task<StreamProbe> ProbeStreamAsync(
        string videoUri, string ratio = "1080p", CancellationToken ct = default)
    {
        string url = BuildPlayUrl(videoUri, ratio);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.douyin.com/");
        req.Headers.TryAddWithoutValidation("Range", "bytes=0-1023");
        req.Headers.TryAddWithoutValidation("User-Agent", _profile.UserAgent);

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            string host = resp.RequestMessage?.RequestUri?.Host ?? "?";
            string type = resp.Content.Headers.ContentType?.MediaType ?? "?";
            return new StreamProbe(
                resp.IsSuccessStatusCode, (int)resp.StatusCode, type,
                sw.ElapsedMilliseconds, host, $"{resp.StatusCode} {type} @{host}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            sw.Stop();
            return new StreamProbe(false, 0, "?", sw.ElapsedMilliseconds, "?", ex.Message);
        }
    }

    // ================= 请求头 =================

    private void AddBrowserHeaders(HttpRequestMessage req, DouyinSession session)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", _profile.UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.douyin.com/");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        req.Headers.TryAddWithoutValidation("sec-ch-ua",
            "\"Not=A?Brand\";v=\"99\", \"Google Chrome\";v=\"151\", \"Chromium\";v=\"151\"");
        req.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        req.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        req.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
        req.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
        req.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
        req.Headers.TryAddWithoutValidation("priority", "u=1, i");
        req.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
    }

    public void Dispose()
    {
        _http.Dispose();
        _sessionGate.Dispose();
    }
}
