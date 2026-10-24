using System.IO;
using System.Text.Json;
using DouyinDesktop.Core;

namespace DouyinDesktop.App;

/// <summary>
/// secsdk 的运行时请求头（`x-tt-session-dtrait` 等）。
///
/// 这些值由浏览器里的 secsdk 脚本**运行时生成**，Cookie 里推不出来
/// （`sdk_source_info` 是加密的 hex，`x_tt_token` 也不是它）。
///
/// 写接口（点赞）缺了它，服务端会**静默丢弃**：
/// 返回 `status_code=0`、界面上看起来成功，但 `user_digged` 一直是 0、点赞数也不动。
/// 实测补 `x-tt-token` 也没用，缺的就是 dtrait。
///
/// 拿法：TemplateFetcher 抓评论请求时顺手把 secsdk 头抄下来存盘。
/// 存一次能用一段时间（服务端会校验时效，过期就重新抓）。
/// </summary>
public static class SecsdkHeaders
{
    private static readonly string FilePath =
        Path.Combine(DouyinPaths.DataDirectory, "secsdk-headers.json");

    private static Dictionary<string, string>? _cache;

    /// <summary>拿现成的头；没有就返回 null（点赞会走"静默失败"那条路）。</summary>
    public static IReadOnlyDictionary<string, string>? Get()
    {
        var map = Load();
        return map.Count > 0 ? map : null;
    }

    /// <summary>抓到的头存盘。只保留 x-tt-session / x-secsdk 前缀的，别把 Cookie 抄进去。</summary>
    public static void Save(IReadOnlyDictionary<string, string> captured)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in captured)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (name.StartsWith("x-tt-session", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("x-secsdk", StringComparison.OrdinalIgnoreCase)
                || name.Equals("x-tt-token", StringComparison.OrdinalIgnoreCase))
            {
                map[name] = value;
            }
        }

        if (map.Count == 0) return;

        try
        {
            Directory.CreateDirectory(DouyinPaths.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(map));
            _cache = map;
            AppLog.Write($"已记录 secsdk 头：{string.Join(", ", map.Keys)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("保存 secsdk 头失败：" + ex.Message);
        }
    }

    private static Dictionary<string, string> Load()
    {
        if (_cache is not null) return _cache;

        try
        {
            if (File.Exists(FilePath))
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
                if (map is not null) return _cache = map;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            AppLog.Write("读取 secsdk 头失败：" + ex.Message);
        }

        return _cache = new Dictionary<string, string>();
    }
}
