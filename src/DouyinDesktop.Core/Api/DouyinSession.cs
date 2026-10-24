using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DouyinDesktop.Core.Api;

/// <summary>
/// 游客凭据。三条东西各管一件事，缺一个都可能被风控：
///   ttwid      —— 设备标识，POST ttwid.bytedance.com 匿名注册，不用登录
///   s_v_web_id —— 指纹 ID，本地按固定格式生成
///   msToken    —— 实测空着也能过；服务端会在响应头 X-Ms-Token 里下发，捡到就存起来
/// 整体落盘，冷启动省掉 2.4 秒的注册往返。
/// </summary>
public sealed class DouyinSession
{
    public string Ttwid { get; set; } = "";
    public string SvWebId { get; set; } = "";
    public string MsToken { get; set; } = "";
    public string UserAgent { get; set; } = DeviceProfile.DefaultUserAgent;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// 扫码登录后从 WebView2 里整包抓下来的 Cookie（"a=1; b=2" 形式）。
    /// 登录态的鉴权全靠它，和 ttwid 那套游客凭据是两回事，可以共存。
    /// </summary>
    public string LoginCookies { get; set; } = "";

    /// <summary>
    /// 登录那一刻用的浏览器 webid。
    ///
    /// 服务端把「账号 ↔ 设备标识」的绑定存在它自己那边。webid / ttwid / s_v_web_id
    /// 只要变一个，这套绑定就失效：登录态还在（评论还能读），但关注流会给填充内容、
    /// 写接口（点赞）静默失败、`profile/self` 直接说"未登录"。
    ///
    /// 存下来是为了启动时能**主动发现**"设备变了"，而不是等用户发现推荐变大众。
    /// 老版本的 session.json 没有这个字段，是空串，那就跳过检查。
    /// </summary>
    public string LoginWebId { get; set; } = "";

    /// <summary>
    /// 服务端在 Set-Cookie 里下发的 UIFID，webSign 签名要用。
    /// 登录 Cookie 里通常只有 UIFID_TEMP，正式的要等第一次请求后才有。
    /// </summary>
    public string Uifid { get; set; } = "";

    /// <summary>从 LoginCookies 里取某个 Cookie 的值（取不到返回空串）。</summary>
    public string CookieValue(string name)
    {
        if (string.IsNullOrEmpty(LoginCookies)) return "";

        string prefix = name + "=";
        foreach (string part in LoginCookies.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string kv = part.Trim();
            if (kv.StartsWith(prefix, StringComparison.Ordinal)) return kv[prefix.Length..];
        }
        return "";
    }

    /// <summary>
    /// uifid：**优先用 Cookie 里的**。
    ///
    /// 会话刷新（ttwid/msToken 过期重建）会重新生成字段值，
    /// 但 Cookie 里那份是登录时抓的、和服务端一致。
    /// 以前优先用字段，刷新后两者对不上，接口就开始返回空。
    /// </summary>
    public string EffectiveUifid
    {
        get
        {
            string fromCookie = CookieValue("UIFID");
            return fromCookie.Length > 0 ? fromCookie : Uifid;
        }
    }

    /// <summary>s_v_web_id（verifyFp / fp 参数）：优先 Cookie，保证和请求头一致。</summary>
    public string EffectiveSvWebId
    {
        get
        {
            string fromCookie = CookieValue("s_v_web_id");
            return fromCookie.Length > 0 ? fromCookie : SvWebId;
        }
    }

    /// <summary>ttwid：优先 Cookie。</summary>
    public string EffectiveTtwid
    {
        get
        {
            string fromCookie = CookieValue("ttwid");
            return fromCookie.Length > 0 ? fromCookie : Ttwid;
        }
    }

    [JsonIgnore] public bool IsLoggedIn => !string.IsNullOrEmpty(LoginCookies);

    [JsonIgnore] public bool IsComplete => !string.IsNullOrEmpty(Ttwid) && !string.IsNullOrEmpty(SvWebId);

    /// <summary>游客凭据用久了要换；登录态不按天数作废，等 Cookie 自己失效。</summary>
    [JsonIgnore] public bool IsStale
        => !IsLoggedIn && DateTimeOffset.Now - CreatedAt > TimeSpan.FromDays(7);

    /// <summary>
    /// 拼给请求头用的完整 Cookie 串。
    ///
    /// 必须按 key 去重：登录时从 WebView2 抓回来的 Cookie 里**本身就带 ttwid / s_v_web_id**，
    /// 如果无脑拼在会话字段后面，请求头里就会出现两个 ttwid、两个 s_v_web_id，
    /// 服务端会认为设备身份前后不一致 → 推荐退化成游客流、评论返回空 body、
    /// 点赞 status_code=0 但实际不生效。
    /// 登录时抓到的是最新的，覆盖会话里的旧值。
    /// </summary>
    [JsonIgnore]
    public string CookieHeader
    {
        get
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            if (!string.IsNullOrEmpty(Ttwid)) map["ttwid"] = Ttwid;
            if (!string.IsNullOrEmpty(SvWebId)) map["s_v_web_id"] = SvWebId;

            foreach (string part in LoginCookies.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string kv = part.Trim();
                int eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                map[kv[..eq]] = kv[(eq + 1)..];
            }

            return string.Join("; ", map.Select(kv => kv.Key + "=" + kv.Value));
        }
    }

    // ================= 生成 =================

    /// <summary>注册一个匿名 ttwid。失败返回空串，调用方自己决定要不要继续。</summary>
    public static async Task<string> RegisterTtwidAsync(
        HttpClient http, string userAgent, CancellationToken ct = default)
    {
        var payload = new
        {
            region = "cn",
            aid = 1768,
            needFid = false,
            service = "www.douyin.com",
            migrate_info = new { ticket = "", source = "node" },
            cbUrlProtocol = "https",
            union = true,
        };

        using var req = new HttpRequestMessage(
            HttpMethod.Post, "https://ttwid.bytedance.com/ttwid/union/register/");
        req.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);

        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (!resp.Headers.TryGetValues("Set-Cookie", out var vals)) return "";

            foreach (string v in vals)
            {
                int idx = v.IndexOf("ttwid=", StringComparison.Ordinal);
                if (idx < 0) continue;
                string tail = v[(idx + 6)..];
                int semi = tail.IndexOf(';');
                string value = semi > 0 ? tail[..semi] : tail;
                if (value.Length > 20) return value;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // 网络问题不致命：没有 ttwid 也常常能过，只是概率低一些
        }
        return "";
    }

    /// <summary>按网页端的格式本地生成 s_v_web_id：verify_{时间36进制}_{随机}...</summary>
    public static string GenerateSvWebId()
    {
        const string alnum = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        long n = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ts36 = new StringBuilder();
        while (n > 0)
        {
            ts36.Insert(0, "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(n % 36)]);
            n /= 36;
        }

        string Rnd(int k) => string.Concat(
            Enumerable.Range(0, k).Select(_ => alnum[Random.Shared.Next(alnum.Length)]));

        return $"verify_{ts36}_{Rnd(8)}_{Rnd(4)}_4{Rnd(3)}_{"89ab"[Random.Shared.Next(4)]}{Rnd(3)}_{Rnd(12)}";
    }

    /// <summary>新建一份完整凭据（会走一次网络注册）。</summary>
    public static async Task<DouyinSession> CreateAsync(
        HttpClient http, DeviceProfile profile, CancellationToken ct = default)
    {
        string ttwid = await RegisterTtwidAsync(http, profile.UserAgent, ct);
        return new DouyinSession
        {
            Ttwid = ttwid,
            SvWebId = GenerateSvWebId(),
            MsToken = "",
            UserAgent = profile.UserAgent,
            CreatedAt = DateTimeOffset.Now,
        };
    }

    // ================= 落盘 =================

    public static string DefaultPath => DouyinPaths.SessionFile;

    public static DouyinSession? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            var s = JsonSerializer.Deserialize<DouyinSession>(File.ReadAllText(path));
            return s is { IsComplete: true, IsStale: false } ? s : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>存盘。返回是否成功——写不进去时调用方要能知道（比如设备基准补记）。</summary>
    public bool Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 存不下就算了，下次重新注册
            return false;
        }
    }
}
