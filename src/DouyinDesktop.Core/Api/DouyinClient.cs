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

        // Set-Cookie 里会下发 UIFID 之类，后续请求带上更稳；只记日志，暂不参与请求
        if (resp.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (string c in cookies)
            {
                int eq = c.IndexOf('=');
                if (eq > 0) Log?.Invoke("Set-Cookie: " + c[..Math.Min(eq, 40)] + "…");
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
