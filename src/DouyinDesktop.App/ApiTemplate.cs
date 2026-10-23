using System.IO;
using System.Text.Json;
using DouyinDesktop.Core;

namespace DouyinDesktop.App;

/// <summary>
/// 接口 URL 模板。
///
/// 背景：抖音对评论 / 作者作品做**强校验**，参数集合和取值都必须和浏览器一致。
/// 我们自己拼参数怎么调都不对（webid 换成浏览器的仍失败），
/// 所以干脆**照抄浏览器发过的那条 URL**，每次只替换 aweme_id / cursor / count
/// 和 msToken，再重新签名。
///
/// 模板存到 data/api-templates.json，平时启动不碰浏览器。
/// </summary>
public static class ApiTemplate
{
    private static readonly string FilePath =
        Path.Combine(DouyinPaths.DataDirectory, "api-templates.json");

    private static Dictionary<string, string>? _cache;

    public static string? Get(string path)
    {
        var map = Load();
        return map.TryGetValue(path, out string? url) && url.Length > 0 ? url : null;
    }

    public static void Save(string path, string url)
    {
        var map = Load();
        map[path] = url;

        try
        {
            Directory.CreateDirectory(DouyinPaths.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(map));
        }
        catch (Exception ex)
        {
            AppLog.Write("保存接口模板失败：" + ex.Message);
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
        catch (Exception ex)
        {
            AppLog.Write("读取接口模板失败：" + ex.Message);
        }

        return _cache = new Dictionary<string, string>();
    }
}
