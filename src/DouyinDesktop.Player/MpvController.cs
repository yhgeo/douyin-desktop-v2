using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace DouyinDesktop.Player;

public sealed class MpvOptions
{
    /// <summary>mpv.exe 的完整路径。</summary>
    public required string MpvPath { get; init; }

    /// <summary>承载画面的父窗口句柄。传 IntPtr.Zero 则不嵌入，mpv 自己开窗口。</summary>
    public IntPtr ParentWindow { get; init; }

    /// <summary>播放 CDN 流必须带 Referer，否则 403。</summary>
    public string Referer { get; init; } = "https://www.douyin.com/";

    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36";

    public int Volume { get; init; } = 100;
    public bool Muted { get; init; }
    public bool HardwareDecode { get; init; } = true;

    /// <summary>mpv 自己的详细日志写到哪。排查"加载失败"这类问题只能靠它。</summary>
    public string? LogPath { get; init; }
}

/// <summary>
/// mpv 进程的遥控器。视频解码、音画同步、缓冲全部交给 mpv，
/// 这边只负责「播哪个地址」和「暂停/音量/进度」。
///
/// 单独进程的好处：mpv 崩了主程序还在，重启一个就行。
/// </summary>
public sealed class MpvController : IAsyncDisposable
{
    private readonly Process _process;
    private readonly MpvIpc _ipc;
    private readonly string _pipeName;
    private bool _disposed;

    /// <summary>
    /// 一条视频的"结束"只上报一次。mpv 会同时发 eof-reached 属性和 end-file 事件，
    /// 两个都转发的话上层会连跳两条。
    /// </summary>
    private volatile bool _endHandled;

    public event Action<double>? TimePosChanged;
    public event Action<double>? DurationChanged;
    public event Action<bool>? PausedChanged;
    public event Action<bool>? BufferingChanged;
    /// <summary>一条视频播完了（自动连播的触发点）。</summary>
    public event Action? PlaybackEnded;
    /// <summary>加载失败（网络/403/格式），和"播完"是两件事，必须分开处理。</summary>
    public event Action<string>? FileFailed;
    /// <summary>新文件加载完成，第一帧已经有了。</summary>
    public event Action? FileLoaded;
    /// <summary>mpv 进程挂了。</summary>
    public event Action<string>? Crashed;
    public event Action<string>? Log;

    public bool IsRunning => !_disposed && !_process.HasExited;
    public double Duration { get; private set; }
    public double Position { get; private set; }
    public bool IsPaused { get; private set; }

    private MpvController(Process process, MpvIpc ipc, string pipeName)
    {
        _process = process;
        _ipc = ipc;
        _pipeName = pipeName;

        _ipc.PropertyChanged += OnPropertyChanged;
        _ipc.EventReceived += OnEvent;
        _ipc.Disconnected += OnDisconnected;
    }

    public static async Task<MpvController> StartAsync(
        MpvOptions options, CancellationToken ct = default)
    {
        if (!File.Exists(options.MpvPath))
            throw new FileNotFoundException("找不到 mpv.exe，先跑 scripts\\fetch-mpv.ps1", options.MpvPath);

        string pipeName = "douyin-mpv-" + Guid.NewGuid().ToString("N")[..12];
        string pipePath = @"\\.\pipe\" + pipeName;

        var psi = new ProcessStartInfo(options.MpvPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(options.MpvPath)!,
        };

        void A(string arg) => psi.ArgumentList.Add(arg);

        if (options.ParentWindow != IntPtr.Zero)
            A("--wid=" + options.ParentWindow.ToInt64().ToString(CultureInfo.InvariantCulture));

        A("--input-ipc-server=" + pipePath);
        A("--idle=yes");                 // 没文件时也保持进程存活
        A("--keep-open=no");             // 播完就进 idle，方便判定"这条结束了"
        A("--no-config");                // 不读用户的 mpv.conf，行为可预测
        A("--no-osc");                   // 不要 mpv 自带的控制条，UI 是我们自己画的
        A("--no-osd-bar");
        A("--osd-level=0");
        A("--really-quiet");
        A("--force-window=no");
        A("--keepaspect=yes");
        A("--panscan=0");
        A("--vo=gpu");
        A(options.HardwareDecode ? "--hwdec=auto-safe" : "--hwdec=no");
        A("--cache=yes");
        A("--demuxer-max-bytes=64MiB");
        A("--demuxer-readahead-secs=15");
        A("--network-timeout=10");
        A("--http-header-fields=Referer: " + options.Referer);
        A("--user-agent=" + options.UserAgent);
        A("--volume=" + options.Volume);
        if (options.Muted) A("--mute=yes");
        A("--input-default-bindings=no"); // 键盘我们自己处理，避免 mpv 抢按键
        A("--input-vo-keyboard=no");
        if (!string.IsNullOrEmpty(options.LogPath))
            A("--log-file=" + options.LogPath);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("mpv 启动失败");

        MpvIpc ipc;
        try
        {
            ipc = await MpvIpc.ConnectAsync(pipeName, TimeSpan.FromSeconds(10), ct);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已经退了 */ }
            process.Dispose();
            throw;
        }

        var controller = new MpvController(process, ipc, pipeName);
        await controller.SubscribeAsync();
        return controller;
    }

    private async Task SubscribeAsync()
    {
        foreach (string prop in new[] { "time-pos", "duration", "pause", "eof-reached", "paused-for-cache" })
        {
            try { await _ipc.ObservePropertyAsync(prop); }
            catch (Exception ex) { Log?.Invoke($"订阅 {prop} 失败：{ex.Message}"); }
        }
    }

    // ================= 控制 =================

    public async Task LoadAsync(string url, bool autoPlay = true)
    {
        Log?.Invoke("loadfile " + url);
        _endHandled = false;
        await _ipc.CommandAsync("loadfile", url, "replace");
        if (autoPlay) await _ipc.CommandAsync("set", "pause", "no");
    }

    // 注意：mpv 的 set 命令只认字符串值。传 JSON 的 true/false/80 会返回
    // "invalid parameter"——实测 0.41 仍然如此，别"优化"成原生类型。
    private static string Flag(bool value) => value ? "yes" : "no";

    public async Task SetPauseAsync(bool paused)
    {
        await _ipc.CommandAsync("set", "pause", Flag(paused));
        IsPaused = paused;
        PausedChanged?.Invoke(paused);
    }

    public Task TogglePauseAsync() => SetPauseAsync(!IsPaused);

    public Task SeekAsync(double seconds)
        => _ipc.CommandAsync("seek", Math.Round(seconds, 3), "absolute");

    public Task SetVolumeAsync(int volume)
        => _ipc.CommandAsync("set", "volume", Math.Clamp(volume, 0, 150).ToString());

    public Task SetMuteAsync(bool muted) => _ipc.CommandAsync("set", "mute", Flag(muted));

    public async Task StopAsync()
    {
        try { await _ipc.CommandAsync("stop"); }
        catch (Exception ex) { Log?.Invoke("stop 失败：" + ex.Message); }
    }

    /// <summary>把 mpv 的绘制区域对齐到新的父窗口尺寸（嵌入模式下由父窗口拉伸，通常不用调）。</summary>
    public Task ResizeAsync() => Task.CompletedTask;

    // ================= 事件 =================

    private void OnPropertyChanged(string name, JsonElement? data)
    {
        switch (name)
        {
            case "time-pos":
                if (data is { ValueKind: JsonValueKind.Number } t)
                {
                    Position = t.GetDouble();
                    TimePosChanged?.Invoke(Position);
                }
                break;

            case "duration":
                if (data is { ValueKind: JsonValueKind.Number } d)
                {
                    Duration = d.GetDouble();
                    DurationChanged?.Invoke(Duration);
                }
                break;

            case "pause":
                if (data is { ValueKind: JsonValueKind.True or JsonValueKind.False } p)
                {
                    IsPaused = p.GetBoolean();
                    PausedChanged?.Invoke(IsPaused);
                }
                break;

            case "paused-for-cache":
                if (data is { ValueKind: JsonValueKind.True or JsonValueKind.False } b)
                    BufferingChanged?.Invoke(b.GetBoolean());
                break;

            case "eof-reached":
                // 播到结尾。真正的"播完"以 end-file(reason=eof) 为准，
                // 这里只是兜底——某些流不会发 end-file。
                if (data is { ValueKind: JsonValueKind.True } && !_endHandled)
                {
                    _endHandled = true;
                    Log?.Invoke("eof-reached");
                    PlaybackEnded?.Invoke();
                }
                break;
        }
    }

    private void OnEvent(string name, JsonElement root)
    {
        switch (name)
        {
            case "file-loaded":
                Log?.Invoke("file-loaded");
                FileLoaded?.Invoke();
                break;

            case "end-file":
            {
                string reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                string detail = root.TryGetProperty("file_error", out var fe) ? fe.GetString() ?? "" : "";
                Log?.Invoke($"end-file: {reason} {detail}");

                // stop / quit 是我们自己发起的，不算"结束"
                if (reason is "stop" or "quit" or "redirect" or "unknown") break;
                if (_endHandled) break;
                _endHandled = true;

                if (reason == "error")
                    FileFailed?.Invoke(string.IsNullOrEmpty(detail) ? "加载失败" : detail);
                else
                    PlaybackEnded?.Invoke();
                break;
            }

            case "idle":
                Log?.Invoke("mpv idle");
                break;
        }
    }

    private void OnDisconnected()
    {
        if (_disposed) return;
        string msg = _process.HasExited
            ? $"mpv 已退出（code {_process.ExitCode}）"
            : "mpv IPC 连接断开";
        Log?.Invoke(msg);
        Crashed?.Invoke(msg);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            await _ipc.CommandAsync("quit");
        }
        catch
        {
            // quit 没回应就直接杀
        }

        await _ipc.DisposeAsync();

        try
        {
            if (!_process.WaitForExit(1500)) _process.Kill(entireProcessTree: true);
        }
        catch { /* 已经退了 */ }
        finally { _process.Dispose(); }
    }
}
