using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DouyinDesktop.Core.Models;

namespace DouyinDesktop.Core.Api;

/// <summary>
/// 本地流代理：只预取每条视频的**开头一小段**，切换时秒出画面，剩下的边播边从 CDN 拉。
///
/// 为什么不能整条预取：实测有一条 19 分钟的视频 207MB，60 秒才下 22MB。
/// 全量下载既慢又浪费，用户看到一半就切走的话更是纯浪费。
///
/// 工作方式：
///   1. 后台对下一条发 Range: bytes=0-{N}，把开头 N 字节留在内存
///   2. mpv 播 http://127.0.0.1:{port}/v/{id}?u={uri}
///   3. 代理先吐内存里的开头段（0 延迟），再从断点向 CDN 续传
///
/// 用 TcpListener 手写 HTTP 而不是 HttpListener：后者绑定端口在非管理员账户下
/// 需要预先注册 URL ACL，会让"双击就能跑"这件事变得很麻烦。
/// </summary>
public sealed class VideoStreamProxy : IAsyncDisposable
{
    /// <summary>预取多少字节。1MB ≈ 1080p 的 2–4 秒，够 mpv 出首帧并建立缓冲；
    /// 再多没意义——CDN 慢的时候 2MB 要下十秒，反而挤占当前视频的带宽。</summary>
    public const int DefaultPrefetchBytes = 1024 * 1024;

    private const int MaxEntries = 40;

    private readonly TcpListener _listener;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, StreamEntry> _entries = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly int _prefetchBytes;
    private readonly string _userAgent;
    private bool _disposed;

    public int Port { get; }

    private VideoStreamProxy(int prefetchBytes, string userAgent)
    {
        _prefetchBytes = prefetchBytes;
        _userAgent = userAgent;

        // 端口传 0 让系统分配，绑完之后再读回来——否则 UrlFor 会拼出 :0
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
        };
        // 长视频要连续读几分钟，不能用默认的 100 秒超时
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public static VideoStreamProxy Start(int prefetchBytes = DefaultPrefetchBytes, string? userAgent = null)
        => new(prefetchBytes, userAgent ?? DeviceProfile.DefaultUserAgent);

    /// <summary>给 mpv 播的本地地址。uri 必须一起带上——代理第一次见到它时才知道去哪拉。</summary>
    public string UrlFor(Aweme item)
    {
        string uri = item.Video?.PlayAddr?.Uri ?? "";
        return $"http://127.0.0.1:{Port}/v/{item.Id}?u={UrlCodec.Quote(uri)}";
    }

    public string UrlFor(string awemeId, string videoUri)
        => $"http://127.0.0.1:{Port}/v/{awemeId}?u={UrlCodec.Quote(videoUri)}";

    /// <summary>这条的开头段是否已经在内存里（切换时用来说明"秒开"的来源）。</summary>
    public bool HasPrefetch(string awemeId)
        => _entries.TryGetValue(awemeId, out var e) && e.HeadLength > 0;

    // ================= 预取 =================

    /// <summary>预取开头一段。已经够了就直接返回，不重复下载。</summary>
    public async Task PrefetchAsync(Aweme item, CancellationToken ct = default)
    {
        string uri = item.Video?.PlayAddr?.Uri ?? "";
        if (string.IsNullOrEmpty(uri) || string.IsNullOrEmpty(item.Id)) return;

        var entry = GetOrCreate(item.Id, DouyinClient.BuildPlayUrl(uri));
        if (entry.HeadLength >= _prefetchBytes) return;

        await entry.Gate.WaitAsync(ct);
        try
        {
            if (entry.HeadLength >= _prefetchBytes) return;
            await FetchHeadAsync(entry, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            // 预取失败不影响播放：真正播放时会走透传
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task FetchHeadAsync(StreamEntry entry, CancellationToken ct)
    {
        using var req = BuildCdnRequest(entry.SourceUrl, 0, _prefetchBytes - 1);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return;

        entry.TotalLength = ResolveTotal(resp);
        entry.ContentType = resp.Content.Headers.ContentType?.MediaType ?? "video/mp4";

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        var ms = new MemoryStream(_prefetchBytes);
        byte[] buf = new byte[64 * 1024];
        while (ms.Length < _prefetchBytes)
        {
            int n = await src.ReadAsync(buf, ct);
            if (n <= 0) break;
            ms.Write(buf, 0, n);
        }

        entry.Head = ms.ToArray();
        entry.HeadLength = entry.Head.Length;
    }

    // ================= 请求处理 =================

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }

            _ = Task.Run(async () =>
            {
                try { await HandleClientAsync(client, _cts.Token); }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    // 客户端提前断开（用户切走了）是常态，不算错误
                }
                finally { try { client.Dispose(); } catch { } }
            });
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

        string? requestLine = await reader.ReadLineAsync(ct);
        if (string.IsNullOrEmpty(requestLine)) return;

        string[] parts = requestLine.Split(' ');
        if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            await WriteSimpleAsync(stream, "405 Method Not Allowed", ct);
            return;
        }

        long? rangeStart = null, rangeEnd = null;
        while (true)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line is null || line.Length == 0) break;
            if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;

            // Range: bytes=start-end（end 可省略）
            string value = line[6..].Trim();
            int eq = value.IndexOf('=');
            if (eq < 0) continue;
            string spec = value[(eq + 1)..];
            int dash = spec.IndexOf('-');
            if (dash < 0) continue;

            if (long.TryParse(spec[..dash], out long s)) rangeStart = s;
            string endPart = spec[(dash + 1)..];
            if (long.TryParse(endPart, out long e)) rangeEnd = e;
            break;
        }

        var (id, uri) = ParseTarget(parts[1]);
        if (string.IsNullOrEmpty(uri))
        {
            await WriteSimpleAsync(stream, "400 Bad Request", ct);
            return;
        }

        var entry = GetOrCreate(id, DouyinClient.BuildPlayUrl(uri));
        entry.LastUsed = DateTime.UtcNow;

        long start = rangeStart ?? 0;
        bool hasRange = rangeStart.HasValue;

        // 命中预取：开头这段直接从内存吐，不用等 CDN
        if (entry.HeadLength > 0 && start < entry.HeadLength)
            await ServeFromHeadAsync(stream, entry, start, rangeEnd, hasRange, ct);
        else
            await PassthroughAsync(stream, entry, start, rangeEnd, hasRange, ct);
    }

    private async Task ServeFromHeadAsync(
        Stream dst, StreamEntry entry, long start, long? rangeEnd, bool hasRange, CancellationToken ct)
    {
        long total = entry.TotalLength;
        long end = rangeEnd ?? (total > 0 ? total - 1 : -1);
        int headEnd = entry.HeadLength;
        int sliceEnd = end > 0 ? (int)Math.Min(headEnd, end + 1) : headEnd;

        if (hasRange)
        {
            long length = end > 0 ? end - start + 1 : -1;
            await WriteHeadAsync(dst, 206, "Partial Content", length, start,
                end > 0 ? end : headEnd - 1, total, entry.ContentType, ct);
        }
        else
        {
            await WriteHeadAsync(dst, 200, "OK", total > 0 ? total : null, null, null, null,
                entry.ContentType, ct);
        }

        if (sliceEnd > start)
            await dst.WriteAsync(entry.Head.AsMemory((int)start, sliceEnd - (int)start), ct);

        // 缓存段之后的部分继续从 CDN 拉
        if (end < 0 || sliceEnd <= end)
            await CopyFromCdnAsync(dst, entry, sliceEnd, end > 0 ? end : null, ct);
    }

    private async Task PassthroughAsync(
        Stream dst, StreamEntry entry, long start, long? rangeEnd, bool hasRange, CancellationToken ct)
    {
        using var req = BuildCdnRequest(entry.SourceUrl, start, rangeEnd);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            await WriteSimpleAsync(dst, $"502 Bad Gateway ({resp.StatusCode})", ct);
            return;
        }

        bool partial = resp.StatusCode == HttpStatusCode.PartialContent;
        long? contentLength = resp.Content.Headers.ContentLength;
        long total = entry.TotalLength > 0 ? entry.TotalLength : ResolveTotal(resp);
        if (total > 0) entry.TotalLength = total;
        entry.ContentType = resp.Content.Headers.ContentType?.MediaType ?? entry.ContentType;

        if (partial || hasRange)
        {
            long end = rangeEnd ?? (total > 0 ? total - 1 : -1);
            await WriteHeadAsync(dst, 206, "Partial Content", contentLength, start,
                end > 0 ? end : null, total, entry.ContentType, ct);
        }
        else
        {
            await WriteHeadAsync(dst, 200, "OK", contentLength, null, null, null,
                entry.ContentType, ct);
        }

        await using var src = await resp.Content.ReadAsStreamAsync(ct);

        // CDN 忽略了 Range 返回 200：得自己跳过前面那段
        if (!partial && start > 0) await SkipAsync(src, start, ct);

        await src.CopyToAsync(dst, 64 * 1024, ct);
    }

    private async Task CopyFromCdnAsync(
        Stream dst, StreamEntry entry, long start, long? end, CancellationToken ct)
    {
        using var req = BuildCdnRequest(entry.SourceUrl, start, end);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return;

        bool partial = resp.StatusCode == HttpStatusCode.PartialContent;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);

        if (!partial && start > 0) await SkipAsync(src, start, ct);
        await src.CopyToAsync(dst, 64 * 1024, ct);
    }

    // ================= HTTP 细节 =================

    private HttpRequestMessage BuildCdnRequest(string url, long start, long? end)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.douyin.com/");
        req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        req.Headers.TryAddWithoutValidation("Range", $"bytes={start}-{(end.HasValue ? end.Value.ToString() : "")}");
        return req;
    }

    private static async Task WriteHeadAsync(
        Stream s, int code, string reason, long? contentLength,
        long? rangeStart, long? rangeEnd, long? totalLength, string contentType, CancellationToken ct)
    {
        long total = totalLength ?? -1;
        var sb = new StringBuilder(256);
        sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Accept-Ranges: bytes\r\n");
        sb.Append("Connection: close\r\n");
        if (contentLength is > 0) sb.Append("Content-Length: ").Append(contentLength.Value).Append("\r\n");
        if (rangeStart.HasValue)
        {
            sb.Append("Content-Range: bytes ").Append(rangeStart.Value).Append('-')
              .Append(rangeEnd ?? (total - 1)).Append('/')
              .Append(total > 0 ? total.ToString() : "*").Append("\r\n");
        }
        sb.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        await s.FlushAsync(ct);
    }

    private static async Task WriteSimpleAsync(Stream s, string status, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(status);
        string head = $"HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await s.WriteAsync(body, ct);
    }

    private static async Task SkipAsync(Stream s, long bytes, CancellationToken ct)
    {
        byte[] buf = new byte[32 * 1024];
        long left = bytes;
        while (left > 0)
        {
            int want = (int)Math.Min(buf.Length, left);
            int n = await s.ReadAsync(buf.AsMemory(0, want), ct);
            if (n <= 0) return;
            left -= n;
        }
    }

    /// <summary>从 Content-Range: bytes 0-2097151/217681858 里取总长度。</summary>
    private static long ResolveTotal(HttpResponseMessage resp)
    {
        if (resp.Content.Headers.ContentRange?.Length is long len) return len;

        // 200 响应没有 Content-Range，Content-Length 就是全长
        if (resp.StatusCode == HttpStatusCode.OK && resp.Content.Headers.ContentLength is long cl) return cl;
        return -1;
    }

    private static (string Id, string Uri) ParseTarget(string rawPath)
    {
        try
        {
            int q = rawPath.IndexOf('?');
            string path = q >= 0 ? rawPath[..q] : rawPath;
            string query = q >= 0 ? rawPath[(q + 1)..] : "";

            string id = path.StartsWith("/v/", StringComparison.Ordinal) ? path[3..] : "";

            string uri = "";
            foreach (string kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!kv.StartsWith("u=", StringComparison.Ordinal)) continue;
                uri = Uri.UnescapeDataString(kv[2..]);
                break;
            }
            return (id, uri);
        }
        catch (UriFormatException)
        {
            return ("", "");
        }
    }

    // ================= 缓存表 =================

    private StreamEntry GetOrCreate(string id, string sourceUrl)
    {
        TrimEntries();
        return _entries.GetOrAdd(id, _ => new StreamEntry(sourceUrl));
    }

    private void TrimEntries()
    {
        if (_entries.Count <= MaxEntries) return;

        foreach (var kv in _entries
                     .OrderBy(x => x.Value.LastUsed)
                     .Take(_entries.Count - MaxEntries))
        {
            if (kv.Value.HeadLength == 0) _entries.TryRemove(kv.Key, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }

        _http.Dispose();
        _cts.Dispose();

        foreach (var e in _entries.Values) e.Gate.Dispose();
        _entries.Clear();
    }

    private sealed class StreamEntry(string sourceUrl)
    {
        public string SourceUrl { get; } = sourceUrl;
        public byte[] Head { get; set; } = Array.Empty<byte>();
        public int HeadLength { get; set; }
        public long TotalLength { get; set; } = -1;
        public string ContentType { get; set; } = "video/mp4";
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
