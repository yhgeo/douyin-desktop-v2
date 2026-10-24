using System.Text;
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
            var previous = _session ?? DouyinSession.Load();
            _session = await DouyinSession.CreateAsync(_http, _profile, ct);
            sw.Stop();

            // 刷新凭据只该换 ttwid / msToken 这些游客材料，
            // **不能把登录态和设备标识弄丢**——之前这里直接新建会话，
            // 于是推荐流失败一次重试就变成游客流，评论/主页也开始返回空。
            //
            // 注意：对**已登录**的会话，这一步实际上把新注册的 ttwid/s_v_web_id
            // 又换回了旧值（设备身份必须和 Cookie 一致，不能随机换）。
            // 所以 forceRefresh 对登录用户的效果≈只递增了一次 refresh_index，
            // 重试仍可能靠这个参数拿到不同的一批内容。
            if (previous is { IsLoggedIn: true })
            {
                _session.LoginCookies = previous.LoginCookies;
                _session.Uifid = previous.Uifid;
                _session.Ttwid = previous.EffectiveTtwid;
                _session.SvWebId = previous.EffectiveSvWebId;
                _session.MsToken = previous.MsToken;
            }

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

        // 只带最近 50 个：全带上会让 URL 越来越长，几千字符容易被服务端拒
        string presented = presentedIds is null
            ? ""
            : string.Join(",", presentedIds.TakeLast(50));
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
        var ps = _profile.FeedParams(count, presentedIds, session.MsToken, refreshIndex,
            session.EffectiveUifid);
        string query = UrlCodec.ToQuery(ps);

        // 签名算的是原始 query；a_bogus 和指纹参数追加在签名之后
        string abogus = _signer.SignQuery(query, "", "www.douyin.com");
        string url = $"{FeedEndpoint}?{query}&a_bogus={UrlCodec.Quote(abogus)}" +
                     $"&verifyFp={session.EffectiveSvWebId}&fp={session.EffectiveSvWebId}";

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

    // ================= 首页 tab 流（实验） =================

    /// <summary>
    /// 网页版首页"推荐"tab 用的接口。module/feed 实测只推视频（58 条 0 图集），
    /// 试试这个能不能拿到图文。
    /// </summary>
    public async Task<FeedResult> GetTabFeedAsync(int count = 20, CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct: ct);
        string bv = _profile.BrowserVersion;

        var ps = new List<KeyValuePair<string, string>>
        {
            new("device_platform", "webapp"),
            new("aid", "6383"),
            new("channel", "channel_pc_web"),
            new("tag_id", ""),
            new("count", count.ToString()),
            new("refresh_index", "1"),
            new("refer_id", ""),
            new("refer_type", "10"),
            new("awemePcRecRawData", "{\"is_client\":false}"),
            new("Seo-Flag", "0"),
            new("install_time", _profile.InstallTime),
            new("pc_client_type", "1"),
            new("update_version_code", "170400"),
            new("version_code", "170400"),
            new("version_name", "17.4.0"),
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
            new("cpu_core_num", _profile.CpuCores.ToString()),
            new("device_memory", _profile.DeviceMemory.ToString()),
            new("platform", "PC"),
            new("downlink", "10"),
            new("effective_type", "4g"),
            new("round_trip_time", "100"),
            new("webid", _profile.EffectiveWebId),
            new("msToken", session.MsToken),
        };

        string query = UrlCodec.ToQuery(ps);
        string abogus = _signer.SignQuery(query, "", "www.douyin.com");
        string url = $"https://www.douyin.com/aweme/v1/web/tab/feed/?{query}" +
                     $"&a_bogus={UrlCodec.Quote(abogus)}" +
                     $"&verifyFp={session.EffectiveSvWebId}&fp={session.EffectiveSvWebId}";

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
                return FeedResult.Fail($"HTTP {(int)resp.StatusCode}", sw.ElapsedMilliseconds);

            // tab 流的响应是 aweme_list（对象数组），不是推荐流的 cards[].aweme 字符串
            var items = ParseAwemeList(body);
            if (items.Count == 0)
            {
                string snippet = body.Length > 200 ? body[..200].Replace('\n', ' ') + "…" : body;
                return FeedResult.Fail($"tab 流没有内容：{snippet}", sw.ElapsedMilliseconds);
            }

            Log?.Invoke($"tab 流 {items.Count} 条 / {sw.ElapsedMilliseconds}ms");
            return new FeedResult(items, true, "", sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            sw.Stop();
            return FeedResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // ================= 推荐流（滚动加载版，实验） =================

    /// <summary>
    /// 复现浏览器滚动加载用的那条推荐接口（参数和 module/feed 完全不同，
    /// 没有 module_id，多了 video_type_select / pull_type=2 / min_window 这些）。
    /// 实测浏览器这条能返回图集。
    /// </summary>
    public async Task<FeedResult> GetFeedScrollAsync(
        int count = 10, int refreshIndex = 2, string endpoint = "tab/feed",
        CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct: ct);
        string bv = _profile.BrowserVersion;

        var ps = new List<KeyValuePair<string, string>>
        {
            new("device_platform", "webapp"),
            new("aid", "6383"),
            new("channel", "channel_pc_web"),
            new("filterGids", ""),
            new("tag_id", ""),
            new("share_aweme_id", ""),
            new("live_insert_type", ""),
            new("count", count.ToString()),
            new("refresh_index", refreshIndex.ToString()),
            new("video_type_select", "1"),
            new("aweme_pc_rec_raw_data",
                "{\"is_client\":false,\"ff_danmaku_status\":1,\"danmaku_switch_status\":1," +
                "\"is_dash_user\":1,\"is_auto_play\":1,\"is_full_screen\":0,\"is_full_webscreen\":0," +
                "\"is_mute\":1,\"is_speed\":1,\"is_visible\":1,\"related_recommend\":1,\"is_xigua_user\":0}"),
            new("globalwid", ""),
            new("pull_type", "2"),
            new("min_window", "0"),
            new("free_right", "0"),
            new("view_count", "2"),
            new("plug_block", "0"),
            new("ug_source", ""),
            new("creative_id", ""),
            new("pc_client_type", "1"),
            new("pc_libra_divert", "Windows"),
            new("support_h265", "0"),
            new("support_dash", "1"),
            new("webcast_sdk_version", "170400"),
            new("webcast_version_code", "170400"),
            new("version_code", "170400"),
            new("version_name", "17.4.0"),
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
            new("cpu_core_num", _profile.CpuCores.ToString()),
            new("device_memory", _profile.DeviceMemory.ToString()),
            new("platform", "PC"),
            new("downlink", "10"),
            new("effective_type", "4g"),
            new("round_trip_time", "50"),
            new("webid", _profile.EffectiveWebId),
            new("msToken", session.MsToken),
        };

        string query = UrlCodec.ToQuery(ps);
        string abogus = _signer.SignQuery(query, "", "www.douyin.com");

        // 浏览器的 verifyFp/fp 在 a_bogus 之后
        query += $"&a_bogus={UrlCodec.Quote(abogus)}" +
                 $"&verifyFp={session.EffectiveSvWebId}&fp={session.EffectiveSvWebId}";

        string url = $"https://www.douyin.com/aweme/v1/web/{endpoint}/?{query}";

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
                return FeedResult.Fail($"HTTP {(int)resp.StatusCode}", sw.ElapsedMilliseconds);

            var items = ParseAwemeList(body);
            if (items.Count == 0) items = ParseFeed(body);

            // 关注流诊断：看服务端到底给了什么（是否把我们当游客）
            Log?.Invoke($"[feed:{endpoint}] {items.Count} 条，长度 {body.Length}，前 120 字：{body[..Math.Min(120, body.Length)]}");

            if (items.Count == 0)
            {
                // 结构对不上时把顶层字段打出来，方便定位
                string shape;
                try
                {
                    using var probe = JsonDocument.Parse(body);
                    shape = "顶层字段：" + string.Join(",", probe.RootElement.EnumerateObject().Select(x => x.Name));
                }
                catch (JsonException) { shape = body.Length > 300 ? body[..300] : body; }

                Log?.Invoke("tab/feed 结构：" + shape);
                return FeedResult.Fail("scroll 流没有内容（" + shape + "）", sw.ElapsedMilliseconds);
            }

            Log?.Invoke($"scroll 流 {items.Count} 条 / {sw.ElapsedMilliseconds}ms");
            return new FeedResult(items, true, "", sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            sw.Stop();
            return FeedResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // ================= 评论 =================

    /// <summary>
    /// 视频弹幕。路径是抓包确认的（网页播放器真实请求）：
    ///   /aweme/v1/web/danmaku/get_v2/   取弹幕
    ///   /aweme/v1/web/danmaku/conf/get/ 取开关配置
    ///
    /// **按 32 秒分段返回**：请求一次只给一段，响应里的 start_time/end_time 就是这段的范围。
    /// 不是每个视频都有弹幕（实测三个里有一个 danmaku_list 是 null）。
    /// </summary>
    public async Task<DanmakuPage> GetDanmakuAsync(
        string awemeId, long startMs, long endMs, CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct: ct);

        var ps = new List<KeyValuePair<string, string>>
        {
            new("device_platform", "webapp"),
            new("aid", "6383"),
            new("channel", "channel_pc_web"),
            new("aweme_id", awemeId),
            new("item_id", awemeId),
            // 单位是毫秒：传秒的话服务端不认，永远返回第一个 32 秒窗口
            new("start_time", startMs.ToString()),
            new("end_time", endMs.ToString()),
        };
        ps.AddRange(_profile.PlatformParams());
        ps.Add(new("webid", _profile.EffectiveWebId));
        ps.Add(new("msToken", session.MsToken));

        string query = UrlCodec.ToQuery(ps);
        string abogus = _signer.SignQuery(query, "", "www.douyin.com");
        string url = $"https://www.douyin.com/aweme/v1/web/danmaku/get_v2/?{query}" +
                     $"&a_bogus={UrlCodec.Quote(abogus)}" +
                     $"&verifyFp={session.EffectiveSvWebId}&fp={session.EffectiveSvWebId}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddBrowserHeaders(req, session);
        req.Headers.Remove("Referer");
        req.Headers.TryAddWithoutValidation("Referer", $"https://www.douyin.com/video/{awemeId}");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log?.Invoke($"弹幕 HTTP {(int)resp.StatusCode}");
                return DanmakuPage.Empty;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            long gotStart = root.TryGetProperty("start_time", out var st) ? st.GetInt64() : 0;
            long gotEnd = root.TryGetProperty("end_time", out var et) ? et.GetInt64() : 0;

            var list = new List<DanmakuItem>();
            if (root.TryGetProperty("danmaku_list", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var item = el.Deserialize<DanmakuItem>();
                    if (item is not null && item.Text.Length > 0) list.Add(item);
                }
            }

            Log?.Invoke($"弹幕 {gotStart / 1000}-{gotEnd / 1000}s：{list.Count} 条");
            return new DanmakuPage(list, gotStart, gotEnd);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            Log?.Invoke("弹幕接口失败：" + ex.Message);
            return DanmakuPage.Empty;
        }
    }

    /// <summary>从 "a=1; b=2" 这种 Cookie 串里取某个键的值。</summary>
    private static string CookieValue(string cookies, string key)
    {
        foreach (string part in cookies.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string kv = part.Trim();
            if (kv.StartsWith(key + "=", StringComparison.Ordinal))
                return kv[(key.Length + 1)..];
        }
        return "";
    }


    /// <summary>
    /// 接口 URL 模板：强校验接口直接照抄浏览器发过的 URL，只替换少量参数再重签。
    /// 由 App 层注入（Core 不依赖 WebView2）。
    /// </summary>
    public Func<string, string?>? GetTemplate { get; set; }

    /// <summary>模板缺失时去抓一次（参数是作品 id），返回是否抓到。</summary>
    public Func<string, Task<bool>>? CaptureTemplate { get; set; }

    /// <summary>
    /// 额外请求头（secsdk 的 `x-tt-session-dtrait` 之类），由 App 层注入。
    ///
    /// 这个头是 secsdk 在浏览器里运行时生成的，Cookie 里推不出来。
    /// 写接口（点赞）缺了它服务端会**静默丢弃**——返回 status_code=0 但状态不变。
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>?>? GetExtraHeaders { get; set; }

    /// <summary>上次尝试抓模板的时间（失败后冷却 5 分钟，避免每次都卡界面）。</summary>
    private DateTime _lastCaptureAttempt = DateTime.MinValue;

    /// <summary>把模板 URL 里的某个参数换成新值（保留其余原样）。</summary>
    private static string SetParam(string query, string name, string value)
    {
        string marker = name + "=";
        string encoded = UrlCodec.Quote(value);

        int i = query.StartsWith(marker, StringComparison.Ordinal)
            ? 0
            : query.IndexOf("&" + marker, StringComparison.Ordinal);

        if (i < 0) return query.Length == 0 ? marker + encoded : query + "&" + marker + encoded;

        int start = i == 0 ? 0 : i + 1;
        int valueStart = start + marker.Length;
        int end = query.IndexOf('&', valueStart);
        if (end < 0) end = query.Length;

        return query[..valueStart] + encoded + query[end..];
    }

    /// <summary>用模板拼出请求 URL：替换 aweme_id / cursor / count / msToken，然后重签。</summary>
    private string? BuildFromTemplate(string template, string awemeId, long cursor, int count,
        DouyinSession session, string? overrideParam = null, string? overrideValue = null)
    {
        int q = template.IndexOf('?');
        if (q < 0) return null;

        string head = template[..(q + 1)];
        string query = template[(q + 1)..];

        int cut = query.IndexOf("&a_bogus=", StringComparison.Ordinal);
        if (cut >= 0) query = query[..cut];
        else if (query.StartsWith("a_bogus=", StringComparison.Ordinal)) query = "";

        if (!string.IsNullOrEmpty(awemeId)) query = SetParam(query, "aweme_id", awemeId);
        if (!string.IsNullOrEmpty(overrideParam) && overrideValue is not null)
            query = SetParam(query, overrideParam, overrideValue);
        else
        {
            query = SetParam(query, "cursor", cursor.ToString());
            query = SetParam(query, "count", count.ToString());
        }

        if (session.MsToken.Length > 0) query = SetParam(query, "msToken", session.MsToken);

        string sign = _signer.SignQuery(query, "", "www.douyin.com");
        return head + query + "&a_bogus=" + UrlCodec.Quote(sign);
    }

    /// <summary>
    /// 点赞 / 取消点赞。type=1 点赞，0 取消。必须登录。
    /// 路径 /aweme/v1/web/commit/item/digg/ 是网页版真实请求。
    /// </summary>
    public async Task<bool> DiggAsync(string awemeId, bool like, CancellationToken ct = default)
        => await DiggRawAsync(awemeId, like, ct) == 0;

    /// <summary>
    /// 登录态失效时触发。
    ///
    /// 抖音对过期的登录态**不返回错误码**，而是静默返回空 body，
    /// 所以只能靠"有 Cookie 但接口拿不到东西"来推断。
    /// 每次会话只报一次，避免刷屏。
    /// </summary>
    public event Action? LoginExpired;

    private bool _loginExpiredReported;

    /// <summary>登录成功后调一下，允许再次报告。</summary>
    public void ResetLoginExpiredFlag() => _loginExpiredReported = false;

    private void ReportLoginExpired()
    {
        if (_loginExpiredReported) return;
        if (Session?.IsLoggedIn != true) return;   // 本来就没登录，不算"失效"

        _loginExpiredReported = true;
        Log?.Invoke("登录态已失效");
        LoginExpired?.Invoke();
    }

    /// <summary>
    /// 校验登录态是否真的还有效。
    ///
    /// 拿一个**不存在的作品 id** 调点赞接口：未登录/失效返回 status_code=8，
    /// 已登录会返回别的错误（作品不存在之类）。不存在的 id 不会产生任何真实点赞，
    /// 所以可以安全地当探针用。
    /// 返回 null 表示网络问题判断不了。
    /// </summary>
    public async Task<bool?> VerifyLoginAsync(CancellationToken ct = default)
    {
        // Session 在第一次请求前是 null。直接返回 false 会让设置页
        // 把"还没加载"显示成"登录已失效，请重新扫码"。
        DouyinSession session;
        try
        {
            session = Session ?? await EnsureSessionAsync(ct: ct);
        }
        catch
        {
            return null;    // 网络问题判断不了
        }

        if (!session.IsLoggedIn) return false;

        int code = await DiggRawAsync("7000000000000000000", true, ct);
        if (code < 0) return null;
        if (code == 8)
        {
            ReportLoginExpired();
            return false;
        }
        return true;
    }

    /// <summary>点赞请求本体，返回 status_code（-1 表示请求失败）。</summary>
    private async Task<int> DiggRawAsync(string awemeId, bool like, CancellationToken ct)
    {
        var session = await EnsureSessionAsync(ct: ct);

        var ps = new List<KeyValuePair<string, string>>
        {
            new("device_platform", "webapp"),
            new("aid", "6383"),
            new("channel", "channel_pc_web"),
            new("aweme_id", awemeId),
            new("item_type", "0"),
            new("type", like ? "1" : "0"),
        };
        ps.AddRange(_profile.PlatformParams());
        ps.Add(new("webid", _profile.EffectiveWebId));
        ps.Add(new("msToken", session.MsToken));

        string query = UrlCodec.ToQuery(ps);
        string abogus = _signer.SignQuery(query, "", "www.douyin.com");
        string url = $"https://www.douyin.com/aweme/v1/web/commit/item/digg/?{query}" +
                     $"&a_bogus={UrlCodec.Quote(abogus)}" +
                     $"&verifyFp={session.EffectiveSvWebId}&fp={session.EffectiveSvWebId}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddBrowserHeaders(req, session);
        AddSecsdkHeaders(req);
        req.Headers.Remove("Referer");
        req.Headers.TryAddWithoutValidation("Referer", $"https://www.douyin.com/video/{awemeId}");

        // 写操作（点赞/关注/评论）必须带 CSRF 头。
        // 优先用**浏览器真实值**（AddSecsdkHeaders 从 secsdk-headers.json 注入的，
        // 实测浏览器发的是 "DOWNGRADE" 而不是 Cookie 里那串 hex）；
        // 没有抓到的值时才回退到 Cookie 里的 passport_csrf_token。
        // 值不对时服务端照样回 status_code=0，但点赞不会真的生效——很难发现。
        if (!req.Headers.Contains("x-secsdk-csrf-token"))
        {
            string csrf = CookieValue(session.LoginCookies, "passport_csrf_token");
            if (csrf.Length > 0)
                req.Headers.TryAddWithoutValidation("x-secsdk-csrf-token", csrf);
        }

        // 试过的、**没用**的补救（2026-10-25 实测，别再重复试）：
        //   · 补 x-tt-token（值取 Cookie 的 x_tt_token）      → 仍然 user_digged=0
        //   · 补 x-tt-session-dtrait                          → 浏览器根本不发这个头
        //   · 把 csrf 换成浏览器真实值 DOWNGRADE               → 仍然 user_digged=0
        // 读接口全部正常、写接口全部静默丢弃，最可能是 TLS 指纹（.NET 的 SChannel
        // 与 Chrome 不同）。要绕过就得换 HTTP 栈，成本高，暂不做。

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log?.Invoke($"点赞 HTTP {(int)resp.StatusCode}");
                return -1;
            }

            using var doc = JsonDocument.Parse(body);
            int code = doc.RootElement.TryGetProperty("status_code", out var sc) ? sc.GetInt32() : -1;
            Log?.Invoke(code == 0
                ? $"点赞 {(like ? "赞" : "取消")}成功"
                : $"点赞 status_code={code}：{body[..Math.Min(240, body.Length)]}");
            return code;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            Log?.Invoke("点赞失败：" + ex.Message);
            return -1;
        }
    }

    public async Task<CommentPage> GetCommentsAsync(
        string awemeId, long cursor = 0, int count = 20, CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct: ct);

        // 参数**完整镜像浏览器**（顺序照抄抓包）。少一个都可能被判无效：
        // 抓包里浏览器发约 40 个参数，我们之前只有 25 个。
        string bv = _profile.BrowserVersion;
        var ps = new List<KeyValuePair<string, string>>
        {
            new("device_platform", "webapp"),
            new("aid", "6383"),
            new("channel", "channel_pc_web"),
            new("aweme_id", awemeId),
            new("pc_img_format", "webp"),
            new("cursor", cursor.ToString()),
            new("count", count.ToString()),
            new("item_type", "0"),
            new("whale_cut_token", ""),
            new("cut_version", "1"),
            new("rcFT", ""),
            new("update_version_code", "170400"),
            new("pc_client_type", "1"),
            new("pc_libra_divert", "Windows"),
            new("support_h265", "0"),
            new("support_dash", "1"),
            new("cpu_core_num", _profile.CpuCores.ToString()),
            new("version_code", "170400"),
            new("version_name", "17.4.0"),
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
            new("webid", _profile.EffectiveWebId),
            new("uifid", session.EffectiveUifid),
            new("verifyFp", session.EffectiveSvWebId),
            new("fp", session.EffectiveSvWebId),
            new("msToken", session.MsToken),
        };

        string query = UrlCodec.ToQuery(ps);
        string abogus = _signer.SignQuery(query, "", "www.douyin.com");
        string url = $"https://www.douyin.com/aweme/v1/web/comment/list/?{query}" +
                     $"&a_bogus={UrlCodec.Quote(abogus)}";

        // 优先用「浏览器模板」拼 URL：强校验接口对参数集合/取值极敏感，
        // 自己拼怎么调都差一点（webid 换成浏览器的仍失败），照抄浏览器最稳。
        const string CommentPath = "/aweme/v1/web/comment/list/";
        string? tpl = GetTemplate?.Invoke(CommentPath);
        if (tpl is null && CaptureTemplate is not null
            && DateTime.UtcNow - _lastCaptureAttempt > TimeSpan.FromMinutes(5))
        {
            // 抓模板要起一次浏览器，失败了不能每次都重来（否则界面一直卡）
            _lastCaptureAttempt = DateTime.UtcNow;
            Log?.Invoke("首次使用，正在抓取接口模板（约 5 秒）…");
            if (await CaptureTemplate(awemeId)) tpl = GetTemplate?.Invoke(CommentPath);
        }

        if (tpl is not null)
        {
            string? fromTemplate = BuildFromTemplate(tpl, awemeId, cursor, count, session);
            if (fromTemplate is not null)
            {
                url = fromTemplate;
                Log?.Invoke("评论使用模板 URL");
            }
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        // 只发最小头集。实测（--webtest）：同样的 URL + 同样的 Cookie，
        // 用「Cookie/UA/Referer/Accept」四个头 → 200 + 两万多字节评论；
        // 用 AddBrowserHeaders 的全套头 → 空 body。
        // 多发 sec-ch-ua / sec-fetch-* / priority 反而被挡。
        req.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
        req.Headers.TryAddWithoutValidation("User-Agent", _profile.UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", $"https://www.douyin.com/video/{awemeId}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

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

            // 注意：空响应**不要**立刻清模板。
            // 连切视频、请求过密都会返回空，清掉只会导致下次又花 5 秒重抓。
            // 模板本身参数是稳定的，留着更划算。

            // 空 body 时把状态码和关键响应头打出来——服务端拒绝的原因通常在这里
            bool emptyBody = body.Length == 0;
            if (page.Comments.Count == 0)
            {
                var hs = resp.Headers
                    .Where(h => h.Key.StartsWith("x-tt", StringComparison.OrdinalIgnoreCase)
                             || h.Key.StartsWith("x-ms", StringComparison.OrdinalIgnoreCase)
                             || h.Key.Contains("ratelimit", StringComparison.OrdinalIgnoreCase)
                             || h.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                    .Select(h => $"{h.Key}={string.Join(",", h.Value)}");
                Log?.Invoke($"评论空响应 HTTP {(int)resp.StatusCode}，长度 {body.Length}，头：{string.Join(" | ", hs)}");
            }

            // 只有**空 body**才是登录态失效的信号。
            // 作品本来没有评论时，服务端会返回一个正常的 JSON（comments 为空数组），
            // 那种情况报"登录失效"是误报——零评论的作品很常见。
            if (emptyBody) ReportLoginExpired();

            Log?.Invoke("[评论URL] " + url);

            Log?.Invoke(page.Comments.Count == 0
                ? $"评论 0 条，原始返回：{body[..Math.Min(300, body.Length)]}"
                : $"评论 {page.Comments.Count} 条 / 共 {page.Total}");
            return page;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log?.Invoke("评论请求失败：" + ex.Message);
            return CommentPage.Empty;
        }
    }

    /// <summary>拉某条评论下的回复。</summary>
    public async Task<CommentPage> GetCommentRepliesAsync(
        string awemeId, string commentId, long cursor = 0, int count = 20, CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct: ct);

        var ps = new List<KeyValuePair<string, string>>
        {
            new("device_platform", "webapp"),
            new("aid", "6383"),
            new("channel", "channel_pc_web"),
            new("item_id", awemeId),
            new("comment_id", commentId),
            new("cursor", cursor.ToString()),
            new("count", count.ToString()),
            new("item_type", "0"),
            new("whale_cut_token", ""),
            new("cut_version", "1"),
            new("rcFT", ""),
        };
        ps.AddRange(_profile.PlatformParams());
        ps.Add(new("webid", _profile.EffectiveWebId));
        ps.Add(new("msToken", session.MsToken));

        string query = UrlCodec.ToQuery(ps);
        string abogus = _signer.SignQuery(query, "", "www.douyin.com");
        string url = $"https://www.douyin.com/aweme/v1/web/comment/list/reply/?{query}" +
                     $"&a_bogus={UrlCodec.Quote(abogus)}" +
                     $"&verifyFp={session.EffectiveSvWebId}&fp={session.EffectiveSvWebId}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddBrowserHeaders(req, session);
        req.Headers.Remove("Referer");
        req.Headers.TryAddWithoutValidation("Referer", $"https://www.douyin.com/video/{awemeId}");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log?.Invoke($"回复 HTTP {(int)resp.StatusCode}");
                return CommentPage.Empty;
            }

            var page = ParseComments(body, cursor, count);
            Log?.Invoke($"回复 {page.Comments.Count} 条");
            return page;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log?.Invoke("回复请求失败：" + ex.Message);
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
        // 强校验接口用浏览器真实 webid（本机随机值会被拒，实测空 body）
        ps.Add(new("webid", _profile.EffectiveWebId));
        if (!string.IsNullOrEmpty(uifid)) ps.Add(new("uifid", uifid));
        ps.Add(new("msToken", session.MsToken));

        string query = UrlCodec.ToQuery(ps);
        string abogus = _signer.SignQuery(query, "", "www.douyin.com");

        // a_bogus 是对不含自己的 query 算的，算完再拼进去；
        // verifyFp/fp 在这个接口里位于 a_bogus 之后（推荐流在之前）
        query += $"&a_bogus={UrlCodec.Quote(abogus)}" +
                 $"&verifyFp={session.EffectiveSvWebId}&fp={session.EffectiveSvWebId}";

        // 作者主页在 secsdk 的 webSign 策略表里，整条 query 还要再签一次
        //
        // 注意：**不要**给这个接口套"浏览器模板"。
        // 视频页不会发出 aweme/post 请求，模板根本抓不到，
        // 每次抓都要白等 14 秒（实测日志：[模板] 一条都没抓到），
        // 表现就是"打开主页卡很久"。这条接口用自己拼的参数一直能正常工作。
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
            {
                Log?.Invoke($"[主页] HTTP {(int)resp.StatusCode}，长度 {body.Length}");
                return FeedResult.Fail($"HTTP {(int)resp.StatusCode}", sw.ElapsedMilliseconds);
            }

            var items = ParseAwemeList(body);

            if (items.Count == 0)
            {
                // 空 body 是登录态失效；**有 body 却解析不出作品**是另一回事
                // （参数/签名被拒、接口结构变了），别混成一句"没有作品"。
                var hs = resp.Headers
                    .Where(h => h.Key.StartsWith("x-tt", StringComparison.OrdinalIgnoreCase)
                             || h.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                    .Select(h => $"{h.Key}={string.Join(",", h.Value)}");
                string shape = body.Length == 0
                    ? "(空 body)"
                    : body[..Math.Min(200, body.Length)].Replace('\n', ' ');

                Log?.Invoke($"[主页] 0 条，HTTP {(int)resp.StatusCode}，长度 {body.Length}，头：{string.Join(" | ", hs)}");
                Log?.Invoke("[主页URL] " + url);

                if (body.Length == 0) ReportLoginExpired();
                return FeedResult.Fail($"没有作品：{shape}", sw.ElapsedMilliseconds);
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
            var root = doc.RootElement;

            // 作者主页 / tab 流：{aweme_list:[...]}
            if (root.TryGetProperty("aweme_list", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var item = a.Deserialize<Aweme>(JsonOptions);
                    if (item is not null && !string.IsNullOrEmpty(item.Id)) list.Add(item);
                }
            }
            // 关注流：{cursor, data:[...], has_more} —— 结构和 aweme_list 完全不同
            else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in data.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;

                    // data 里可能是 {aweme:{...}}，也可能直接是作品对象
                    var node = a.TryGetProperty("aweme", out var inner)
                               && inner.ValueKind == JsonValueKind.Object
                        ? inner
                        : a;

                    var item = node.Deserialize<Aweme>(JsonOptions);
                    if (item is not null && !string.IsNullOrEmpty(item.Id)) list.Add(item);
                }
            }
            // 作品详情：{aweme_detail:{...}}
            else if (root.TryGetProperty("aweme_detail", out var detail)
                     && detail.ValueKind == JsonValueKind.Object)
            {
                var item = detail.Deserialize<Aweme>(JsonOptions);
                if (item is not null && !string.IsNullOrEmpty(item.Id)) list.Add(item);
            }
        }
        catch (JsonException) { }
        return list;
    }

    /// <summary>
    /// 作品详情。需要 webSign 签名，返回单条作品的完整结构。
    ///
    /// 注意：**PC 端接口没有 IP 属地**。曾经以为详情接口会给，实测（把 109KB
    /// 完整响应存盘后 grep）列表和详情里都没有 ip_label，region 恒为空，
    /// 登录态下也一样。评论区倒是有 ip_label。
    /// 成本：每个作品一次请求，所以调用方要自己做防抖和缓存。
    /// </summary>
    public async Task<Aweme?> GetAwemeDetailAsync(string awemeId, CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct: ct);

        var ps = new List<KeyValuePair<string, string>>
        {
            new("device_platform", "webapp"),
            new("aid", "6383"),
            new("channel", "channel_pc_web"),
            new("aweme_id", awemeId),
        };
        ps.AddRange(_profile.PlatformParams());
        ps.Add(new("webid", _profile.EffectiveWebId));

        string uifid = session.EffectiveUifid;
        if (!string.IsNullOrEmpty(uifid)) ps.Add(new("uifid", uifid));
        ps.Add(new("msToken", session.MsToken));

        string query = UrlCodec.ToQuery(ps);
        string abogus = _signer.SignQuery(query, "", "www.douyin.com");
        query += $"&a_bogus={UrlCodec.Quote(abogus)}" +
                 $"&verifyFp={session.EffectiveSvWebId}&fp={session.EffectiveSvWebId}";

        // 详情接口在 secsdk 的 webSign 策略表里
        string url = WebSign.SignUrl(
            "https://www.douyin.com/aweme/v1/web/aweme/detail/", query, uifid);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddBrowserHeaders(req, session);

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log?.Invoke($"详情 HTTP {(int)resp.StatusCode}");
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("aweme_detail", out var d)
                || d.ValueKind != JsonValueKind.Object)
            {
                Log?.Invoke("详情里没有 aweme_detail");
                return null;
            }

            var item = d.Deserialize<Aweme>(JsonOptions);
            Log?.Invoke(d.TryGetProperty("user_digged", out var ud)
                ? $"详情 ok，user_digged={ud}"
                : "详情 ok，但没有 user_digged 字段");
            return item;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log?.Invoke("详情请求失败：" + ex.Message);
            return null;
        }
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

    /// <summary>
    /// 浏览器常用头。高风控接口缺了关键的 secsdk 头会被**静默丢弃**
    /// （点赞返回 status_code=0 但没生效、评论/详情返回空 body）。
    /// secsdk 头从 <see cref="GetExtraHeaders"/> 补。
    /// </summary>
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

    /// <summary>
    /// 给写接口补 secsdk 头（浏览器真实值）。
    /// 只在点赞这类写操作上生效——读接口带上反而多一层变数。
    /// </summary>
    private void AddSecsdkHeaders(HttpRequestMessage req)
    {
        if (GetExtraHeaders?.Invoke() is not { Count: > 0 } extra) return;

        foreach (var (name, value) in extra)
        {
            if (string.IsNullOrEmpty(value)) continue;
            req.Headers.Remove(name);
            req.Headers.TryAddWithoutValidation(name, value);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _sessionGate.Dispose();
    }
}
