using System.Text;
using System.Text.Json;

namespace DouyinDesktop.Core.Api;

/// <summary>
/// 预取片段的**磁盘**缓存：上次预取过的视频开头，下次启动还能直接用。
///
/// 只缓存开头 1MB（和内存预取同样的量），不是整条视频——
/// 整条动辄几百 MB，落盘既慢又占地方，收益也基本为零（用户看不到那么后面）。
///
/// 文件格式（每条一个文件）：
///   [4 字节 "DYPF"][4 字节头长度，小端][头 JSON][原始字节]
/// 用单文件而不是「数据 + 元信息」两个文件，避免写到一半崩掉后两者不一致。
///
/// 防堆积：
///   - 总量上限（默认 120MB）
///   - 条数上限（默认 120 条）
///   - 超过 7 天的直接删
///   - 每次启动整理一次，之后每写 20 条整理一次
/// </summary>
public sealed class PrefetchCache
{
    public const long DefaultMaxBytes = 120L * 1024 * 1024;
    public const int DefaultMaxEntries = 120;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private const string Magic = "DYPF";
    private const int HeaderSizeBytes = 8;
    private const int TrimEverySaves = 8;

    private readonly string _dir;
    private readonly long _maxBytes;
    private readonly int _maxEntries;
    private int _savesSinceTrim;
    private readonly object _gate = new();

    private PrefetchCache(string dir, long maxBytes, int maxEntries)
    {
        _dir = dir;
        _maxBytes = maxBytes;
        _maxEntries = maxEntries;
    }

    /// <summary>打开缓存目录。目录建不出来就返回 null——缓存是优化，不能影响主流程。</summary>
    public static PrefetchCache? TryOpen(
        string? dir = null, long maxBytes = DefaultMaxBytes, int maxEntries = DefaultMaxEntries)
    {
        string target = dir ?? DouyinPaths.PrefetchDirectory;
        try
        {
            Directory.CreateDirectory(target);
            var cache = new PrefetchCache(target, maxBytes, maxEntries);

            // 清理扔后台：启动时同步遍历 + 删除上百个文件会拖慢窗口显示，
            // 而缓存清理晚一两秒完全不影响播放（Trim 自己吞异常）。
            _ = Task.Run(cache.Trim);

            return cache;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public readonly record struct CachedHead(byte[] Data, long TotalLength, string ContentType);

    /// <summary>
    /// 取缓存。会校验「文件路径」是否一致——同一个作品换了 CDN 节点没关系，
    /// 但换了一条流（不同清晰度/重新编码）就不能用，否则 mp4 头部对不上，播放会花屏。
    /// </summary>
    public bool TryLoad(string awemeId, string sourceUrl, out CachedHead head)
    {
        head = default;
        if (string.IsNullOrEmpty(awemeId)) return false;

        try
        {
            string file = FilePath(awemeId);
            if (!File.Exists(file)) return false;

            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var prefix = new byte[HeaderSizeBytes];
            if (fs.Read(prefix, 0, HeaderSizeBytes) != HeaderSizeBytes) return false;
            if (Encoding.ASCII.GetString(prefix, 0, 4) != Magic) return false;

            int headerLen = BitConverter.ToInt32(prefix, 4);
            if (headerLen <= 0 || headerLen > 64 * 1024) return false;

            var headerBytes = new byte[headerLen];
            if (fs.Read(headerBytes, 0, headerLen) != headerLen) return false;

            var meta = JsonSerializer.Deserialize<Header>(headerBytes);
            if (meta is null) return false;

            // 换了流就别用
            if (!string.Equals(meta.Key, PathKey(sourceUrl), StringComparison.Ordinal)) return false;

            long remain = fs.Length - HeaderSizeBytes - headerLen;
            if (remain <= 0 || remain > 4 * 1024 * 1024) return false;

            var data = new byte[remain];
            int read = 0;
            while (read < data.Length)
            {
                int n = fs.Read(data, read, data.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < data.Length) return false;

            // 摸一下访问时间，给淘汰策略用
            try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } catch (IOException) { }

            head = new CachedHead(data, meta.Total, meta.Type);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>存一条。先写临时文件再改名，避免半截文件被当成有效缓存。</summary>
    public void Save(string awemeId, string sourceUrl, byte[] data, long totalLength, string contentType)
    {
        if (string.IsNullOrEmpty(awemeId) || data.Length == 0) return;

        try
        {
            var meta = new Header
            {
                Key = PathKey(sourceUrl),
                Total = totalLength,
                Type = contentType,
                Saved = DateTime.UtcNow,
            };
            byte[] headerBytes = JsonSerializer.SerializeToUtf8Bytes(meta);

            string file = FilePath(awemeId);
            string tmp = file + ".tmp";

            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(Encoding.ASCII.GetBytes(Magic));
                fs.Write(BitConverter.GetBytes(headerBytes.Length));
                fs.Write(headerBytes);
                fs.Write(data);
            }

            File.Move(tmp, file, overwrite: true);

            lock (_gate)
            {
                if (++_savesSinceTrim >= TrimEverySaves)
                {
                    _savesSinceTrim = 0;
                    Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去就算了，下次重新预取
        }
    }

    /// <summary>按「总量 / 条数 / 年龄」三道上限清理。</summary>
    public void Trim()
    {
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles("*.bin");
            if (files.Length == 0) return;

            DateTime cutoff = DateTime.UtcNow - MaxAge;
            var kept = new List<FileInfo>(files.Length);
            long total = 0;

            // 新 -> 旧
            foreach (var f in files.OrderByDescending(x => x.LastWriteTimeUtc))
            {
                if (f.LastWriteTimeUtc < cutoff) { TryDelete(f); continue; }
                kept.Add(f);
                total += f.Length;
            }

            // 超量就从最旧的开始丢
            for (int i = kept.Count - 1; i >= 0 && (total > _maxBytes || kept.Count > _maxEntries); i--)
            {
                total -= kept[i].Length;
                TryDelete(kept[i]);
                kept.RemoveAt(i);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>当前缓存占用（字节），设置页/日志用。</summary>
    public (int Count, long Bytes) Stats()
    {
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles("*.bin");
            return (files.Length, files.Sum(f => f.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    private string FilePath(string awemeId) => Path.Combine(_dir, awemeId + ".bin");

    private static void TryDelete(FileInfo f)
    {
        try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 用「路径」而不是完整 URL 做标识。
    /// 同一条视频换 CDN 节点时，host 和鉴权参数（query）都会变，
    /// 但路径指向的是同一个文件——只比路径才不会白白丢掉缓存。
    /// 反过来，换了清晰度/重新编码时路径会变，那就自动不命中，安全。
    /// </summary>
    private static string PathKey(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";

        int q = url.IndexOf('?');
        string noQuery = q >= 0 ? url[..q] : url;

        int scheme = noQuery.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return noQuery;

        int slash = noQuery.IndexOf('/', scheme + 3);
        return slash >= 0 ? noQuery[slash..] : noQuery;
    }

    private sealed class Header
    {
        public string Key { get; set; } = "";
        public long Total { get; set; } = -1;
        public string Type { get; set; } = "video/mp4";
        public DateTime Saved { get; set; }
    }
}
