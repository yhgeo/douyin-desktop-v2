using System.Text;

namespace DouyinDesktop.Core.Api;

/// <summary>
/// 伪装成网页端的设备信息。签名算法会把这些参数一起算进 a_bogus，
/// 所以值本身不敏感，但**顺序和数量**必须稳定——同一个会话里不要变。
/// </summary>
public sealed class DeviceProfile
{
    public string UserAgent { get; init; } = DefaultUserAgent;

    public int ScreenWidth { get; init; } = 2560;
    public int ScreenHeight { get; init; } = 1440;
    public int CpuCores { get; init; } = Math.Max(4, Environment.ProcessorCount);
    public int DeviceMemory { get; init; } = 32;
    public string WebId { get; init; } = RandomDigits(19);
    public string InstallTime { get; init; } = "1715480185";

    /// <summary>
    /// 推荐流的 module_id。3003101 是默认"推荐"。
    /// 传空字符串表示不带这个参数（用于实验：确认它是否过滤了图文）。
    /// </summary>
    public string ModuleId { get; init; } = "3003101";

    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36";

    /// <summary>浏览器大版本号，从 UA 里取，保证和 UA 自洽。</summary>
    public string BrowserVersion
    {
        get
        {
            int i = UserAgent.IndexOf("Chrome/", StringComparison.Ordinal);
            if (i < 0) return "151.0.0.0";
            int start = i + 7, end = UserAgent.IndexOf(' ', start);
            return end < 0 ? UserAgent[start..] : UserAgent[start..end];
        }
    }

    public static string RandomDigits(int n)
    {
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++) sb.Append((char)('0' + Random.Shared.Next(10)));
        return sb.ToString();
    }

    /// <summary>
    /// 通用平台参数。推荐流之外的接口（评论等）都用这一组，
    /// 对应参考实现里的 with_platform()。
    /// </summary>
    public List<KeyValuePair<string, string>> PlatformParams()
    {
        string bv = BrowserVersion;
        return new List<KeyValuePair<string, string>>
        {
            new("pc_client_type", "1"),
            new("version_code", "170400"),
            new("version_name", "17.4.0"),
            new("cookie_enabled", "true"),
            new("screen_width", ScreenWidth.ToString()),
            new("screen_height", ScreenHeight.ToString()),
            new("browser_language", "zh-CN"),
            new("browser_platform", "Win32"),
            new("browser_name", "Chrome"),
            new("browser_version", bv),
            new("browser_online", "true"),
            new("engine_name", "Blink"),
            new("engine_version", bv),
            new("os_name", "Windows"),
            new("os_version", "10"),
            new("cpu_core_num", CpuCores.ToString()),
            new("device_memory", DeviceMemory.ToString()),
            new("platform", "PC"),
            new("downlink", "10"),
            new("effective_type", "4g"),
            new("round_trip_time", "0"),
        };
    }

    /// <summary>
    /// 推荐流参数。顺序敏感——签名算的是这个顺序的原始串，改顺序签名就对不上。
    /// presented_ids 用来让服务端避开已推过的内容。
    /// </summary>
    public List<KeyValuePair<string, string>> FeedParams(
        int count, string presentedIds, string msToken, int refreshIndex)
    {
        string bv = BrowserVersion;

        var ps = new List<KeyValuePair<string, string>>
        {
            new("device_platform", "webapp"),
            new("aid", "6383"),
            new("channel", "channel_pc_web"),
        };

        // 实验用：ModuleId 为空时整个参数都不发
        if (!string.IsNullOrEmpty(ModuleId))
            ps.Add(new("module_id", ModuleId));

        ps.AddRange(new[]
        {
            new KeyValuePair<string, string>("count", count.ToString()),
            new("filterGids", ""),
            new("presented_ids", presentedIds),
            new("refresh_index", refreshIndex.ToString()),
            new("refer_id", ""),
            new("refer_type", "10"),
            new("awemePcRecRawData", "{\"is_client\":false}"),
            new("Seo-Flag", "0"),
            new("install_time", InstallTime),
            new("pc_client_type", "1"),
            new("update_version_code", "170400"),
            new("version_code", "170400"),
            new("version_name", "17.4.0"),
            new("cookie_enabled", "true"),
            new("screen_width", ScreenWidth.ToString()),
            new("screen_height", ScreenHeight.ToString()),
            new("browser_language", "zh-CN"),
            new("browser_platform", "Win32"),
            new("browser_name", "Chrome"),
            new("browser_version", bv),
            new("browser_online", "true"),
            new("engine_name", "Blink"),
            new("engine_version", bv),
            new("os_name", "Windows"),
            new("os_version", "10"),
            new("cpu_core_num", CpuCores.ToString()),
            new("device_memory", DeviceMemory.ToString()),
            new("platform", "PC"),
            new("downlink", "10"),
            new("effective_type", "4g"),
            new("round_trip_time", "100"),
            new("webid", WebId),
            new("msToken", msToken),
        });

        return ps;
    }
}
