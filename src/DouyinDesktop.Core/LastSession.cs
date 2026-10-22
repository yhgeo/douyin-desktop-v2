using System.IO.Compression;
using System.Text.Json;
using DouyinDesktop.Core.Models;

namespace DouyinDesktop.Core;

/// <summary>
/// 上次退出时的现场：推荐流的列表 + 看到第几条。
///
/// 为什么存整个列表而不是只存 id：启动时如果只拿到 id，还是得再请求一次接口
/// 才能播——那就没意义了。存下整条数据，启动可以直接开播，一次请求都不用。
///
/// 只存前 30 条：够接着往下看，文件也不会膨胀（每条带播放地址，几 KB）。
/// </summary>
public sealed class LastSessionSnapshot
{
    public int Version { get; set; } = 1;
    public DateTime SavedAt { get; set; }
    public int Index { get; set; }
    public List<Aweme> Items { get; set; } = new();
}

public static class LastSession
{
    public const int MaxItems = 30;

    /// <summary>最多认多久之前的现场。再久的推荐内容太陈旧，不如重新拉。</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(48);

    public static void Save(int index, IReadOnlyList<Aweme> items, string? path = null)
    {
        if (items.Count == 0) return;

        try
        {
            var snap = new LastSessionSnapshot
            {
                SavedAt = DateTime.UtcNow,
                Index = Math.Clamp(index, 0, items.Count - 1),
                Items = items.Take(MaxItems).ToList(),
            };

            string target = path ?? DouyinPaths.LastSessionFile;
            string? dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 每条作品带一堆播放地址，纯 JSON 30 条约 1.5MB，切一次写一次太浪费。
            // GZip 之后大概 1/10，够快了。
            string tmp = target + ".tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            {
                JsonSerializer.Serialize(gz, snap);
            }
            File.Move(tmp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 存不下就算了，下次正常拉推荐
        }
    }

    public static LastSessionSnapshot? Load(TimeSpan? maxAge = null, string? path = null)
    {
        try
        {
            string target = path ?? DouyinPaths.LastSessionFile;
            if (!File.Exists(target)) return null;

            LastSessionSnapshot? snap;
            using (var fs = File.OpenRead(target))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            {
                snap = JsonSerializer.Deserialize<LastSessionSnapshot>(gz);
            }

            if (snap is null || snap.Version != 1) return null;
            if (snap.Items.Count == 0) return null;
            if (DateTime.UtcNow - snap.SavedAt > (maxAge ?? MaxAge)) return null;

            return snap;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Clear(string? path = null)
    {
        try { File.Delete(path ?? DouyinPaths.LastSessionFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
