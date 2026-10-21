using System.Diagnostics;
using System.Text.Json;
using DouyinDesktop.Core.Api;
using DouyinDesktop.Player;

// 播放器行为探针。两个用途：
//   ipc    —— 逐条测试 IPC 命令，定位 "invalid parameter" 到底出在哪条
//   switch —— 对比「loadfile replace」和「playlist + 预读」的切换耗时
//
// 跑法：dotnet run --project src/DouyinDesktop.PlayerProbe -- ipc

string mode = args.ElementAtOrDefault(0) ?? "ipc";
string mpvPath = FindMpv();
Console.WriteLine($"mpv: {mpvPath}\nmode: {mode}\n");

string pipeName = "probe-" + Guid.NewGuid().ToString("N")[..8];
var psi = new ProcessStartInfo(mpvPath)
{
    UseShellExecute = false,
    CreateNoWindow = true,
    WorkingDirectory = Path.GetDirectoryName(mpvPath)!,
};
psi.ArgumentList.Add("--idle=yes");
psi.ArgumentList.Add("--input-ipc-server=" + @"\\.\pipe\" + pipeName);
psi.ArgumentList.Add("--no-config");
psi.ArgumentList.Add("--really-quiet");
psi.ArgumentList.Add("--vo=null");   // 不弹窗，只看协议
psi.ArgumentList.Add("--ao=null");
if (mode is "switch" or "proxy")
{
    // 真实解码路径才测得出切换耗时
    psi.ArgumentList.Remove("--vo=null");
    psi.ArgumentList.Remove("--ao=null");
    psi.ArgumentList.Add("--vo=gpu");
    psi.ArgumentList.Add("--hwdec=auto-safe");
    psi.ArgumentList.Add("--cache=yes");
    psi.ArgumentList.Add("--demuxer-max-bytes=64MiB");
    psi.ArgumentList.Add("--http-header-fields=Referer: https://www.douyin.com/");
}

using var process = Process.Start(psi)!;
await using var ipc = await MpvIpc.ConnectAsync(pipeName, TimeSpan.FromSeconds(10));

try
{
    if (mode == "ipc") await TestCommands(ipc);
    else if (mode == "proxy") await TestProxy(ipc);
    else await TestSwitch(ipc);
}
finally
{
    try { await ipc.CommandAsync("quit"); } catch { /* 忽略 */ }
    if (!process.WaitForExit(1500)) process.Kill(true);
}

return 0;

// ============================================================

static async Task TestCommands(MpvIpc ipc)
{
    var cases = new (string Name, object[] Cmd)[]
    {
        ("set pause true(bool)",        new object[] { "set", "pause", true }),
        ("set pause false(bool)",       new object[] { "set", "pause", false }),
        ("set pause yes(string)",       new object[] { "set", "pause", "yes" }),
        ("get_property pause",          new object[] { "get_property", "pause" }),
        ("set volume 80(int)",          new object[] { "set", "volume", 80 }),
        ("seek 5 absolute",             new object[] { "seek", 5, "absolute" }),
        ("observe time-pos",            new object[] { "observe_property", 101, "time-pos" }),
        ("observe pause",               new object[] { "observe_property", 102, "pause" }),
        ("set mute yes(string)",        new object[] { "set", "mute", "yes" }),
    };

    foreach (var (name, cmd) in cases)
    {
        try
        {
            var data = await ipc.CommandAsync(cmd);
            string shown = data is null ? "null" : data.Value.ValueKind == JsonValueKind.Null
                ? "null" : data.Value.ToString();
            Console.WriteLine($"  OK    {name,-28} -> {Shorten(shown, 40)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {name,-28} -> {ex.Message}");
        }
    }
}

static async Task TestProxy(MpvIpc ipc)
{
    Console.WriteLine("取播放地址…");
    using var client = DouyinClient.Create();

    // 推荐流里混着图集/直播卡片，多翻几批凑够样本
    var items = new List<DouyinDesktop.Core.Models.Aweme>();
    for (int page = 0; page < 4 && items.Count < 6; page++)
    {
        var f = await client.GetFeedAsync(10, items.Select(x => x.Id));
        if (!f.Success) { Console.WriteLine("拉取失败：" + f.Message); break; }
        items.AddRange(f.Items.Where(x => x.IsPlayable));
        Console.WriteLine($"  第 {page + 1} 批：可播 {items.Count} 条");
    }
    if (items.Count < 6) { Console.WriteLine("可播样本不足"); return; }

    var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    ipc.EventReceived += (name, _) => { if (name == "file-loaded") loaded.TrySetResult(true); };

    async Task<double> TimeLoad(string url)
    {
        loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = Stopwatch.StartNew();
        await ipc.CommandAsync("loadfile", url, "replace");
        var done = await Task.WhenAny(loaded.Task, Task.Delay(20000));
        sw.Stop();
        return done == loaded.Task ? sw.Elapsed.TotalMilliseconds : -1;
    }

    // ---- A：直连 CDN ----
    Console.WriteLine("[A] 直连 CDN");
    var a = new List<double>();
    for (int i = 0; i < 3; i++)
    {
        double ms = await TimeLoad(DouyinClient.BuildPlayUrl(items[i].Video!.PlayAddr!.Uri));
        a.Add(ms);
        Console.WriteLine($"    #{i} {(ms < 0 ? "超时" : ms.ToString("0") + "ms")}");
        await Task.Delay(1000);
    }

    // ---- B：本地代理（开头已预取）----
    await using var proxy = VideoStreamProxy.Start();
    Console.WriteLine($"\n[B] 本地代理（端口 {proxy.Port}，预取开头 1MB）");
    var b = new List<double>();
    for (int i = 3; i < 6; i++)
    {
        var swPrefetch = Stopwatch.StartNew();
        await proxy.PrefetchAsync(items[i]);
        swPrefetch.Stop();
        bool hit = proxy.HasPrefetch(items[i].Id);
        Console.WriteLine($"    预取 {(hit ? "完成" : "失败")} {swPrefetch.ElapsedMilliseconds}ms");

        double ms = await TimeLoad(proxy.UrlFor(items[i]));
        b.Add(ms);
        Console.WriteLine($"    #{i} {(ms < 0 ? "超时" : ms.ToString("0") + "ms")}");
        await Task.Delay(1000);
    }

    Console.WriteLine($"\n结果：直连平均 {Avg(a):0}ms   代理平均 {Avg(b):0}ms");
}

static async Task TestSwitch(MpvIpc ipc)
{
    Console.WriteLine("取播放地址…");
    using var client = DouyinClient.Create();
    var feed = await client.GetFeedAsync(8);
    if (!feed.Success) { Console.WriteLine("拿不到推荐流：" + feed.Message); return; }

    var urls = feed.Items.Where(x => x.IsPlayable)
        .Select(x => DouyinClient.BuildPlayUrl(x.Video!.PlayAddr!.Uri)).ToList();
    Console.WriteLine($"拿到 {urls.Count} 条地址\n");
    if (urls.Count < 5) return;

    var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    ipc.EventReceived += (name, _) => { if (name == "file-loaded") loaded.TrySetResult(true); };

    async Task<double> TimeLoad(object[] cmd)
    {
        loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = Stopwatch.StartNew();
        await ipc.CommandAsync(cmd);
        var done = await Task.WhenAny(loaded.Task, Task.Delay(15000));
        sw.Stop();
        return done == loaded.Task ? sw.Elapsed.TotalMilliseconds : -1;
    }

    // ---- A：每次都 loadfile replace（当前实现）----
    Console.WriteLine("[A] loadfile replace（当前做法）");
    var aTimes = new List<double>();
    for (int i = 0; i < 4; i++)
    {
        double ms = await TimeLoad(new object[] { "loadfile", urls[i], "replace" });
        aTimes.Add(ms);
        Console.WriteLine($"    #{i} {(ms < 0 ? "超时" : ms.ToString("0") + "ms")}");
        await Task.Delay(1200);
    }

    // ---- B：playlist + prefetch-playlist ----
    Console.WriteLine("\n[B] playlist + prefetch-playlist=yes");
    await ipc.CommandAsync("set", "prefetch-playlist", "yes");
    await TimeLoad(new object[] { "loadfile", urls[4], "replace" });
    for (int i = 5; i < 8; i++) await ipc.CommandAsync("loadfile", urls[i], "append");

    var bTimes = new List<double>();
    for (int i = 0; i < 3; i++)
    {
        await Task.Delay(1200);
        double ms = await TimeLoad(new object[] { "playlist-next", "force" });
        bTimes.Add(ms);
        Console.WriteLine($"    #{i} {(ms < 0 ? "超时" : ms.ToString("0") + "ms")}");
    }

    Console.WriteLine($"\n结果：replace 平均 {Avg(aTimes):0}ms   playlist-next 平均 {Avg(bTimes):0}ms");
}

static double Avg(List<double> xs)
{
    var ok = xs.Where(x => x >= 0).ToList();
    return ok.Count == 0 ? -1 : ok.Average();
}

static string Shorten(string s, int n) => s.Length <= n ? s : s[..n] + "…";

static string FindMpv()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
    {
        string p = Path.Combine(dir.FullName, "assets", "mpv", "mpv.exe");
        if (File.Exists(p)) return p;
    }
    throw new FileNotFoundException("找不到 assets/mpv/mpv.exe");
}
