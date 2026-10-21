using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DouyinDesktop.Player;

/// <summary>
/// mpv 的 JSON IPC 客户端。协议是「一行一个 JSON 对象」：
///   请求  {"command": [...], "request_id": 1}
///   回复  {"request_id": 1, "error": "success", "data": ...}
///   事件  {"event": "property-change", "name": "time-pos", "data": 12.3}
/// 请求和回复靠 request_id 配对，事件没有 id。
/// </summary>
public sealed class MpvIpc : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly Task _readLoop;

    private int _nextId;

    /// <summary>属性变化（observe_property 订阅的那些）。</summary>
    public event Action<string, JsonElement?>? PropertyChanged;

    /// <summary>其它事件（file-loaded、end-file 等），参数是事件名。</summary>
    public event Action<string, JsonElement>? EventReceived;

    /// <summary>连接断了（mpv 崩了或被杀）。</summary>
    public event Action? Disconnected;

    private MpvIpc(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public static async Task<MpvIpc> ConnectAsync(
        string pipeName, TimeSpan timeout, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await pipe.ConnectAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            pipe.Dispose();
            throw new TimeoutException($"连接 mpv IPC 管道超时（{pipeName}）");
        }

        return new MpvIpc(pipe);
    }

    /// <summary>发一条命令，等 mpv 回话。返回 data 字段（可能是 null）。</summary>
    public async Task<JsonElement?> CommandAsync(params object[] args)
    {
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        string json = JsonSerializer.Serialize(new { command = args, request_id = id });
        try
        {
            await _writer.WriteLineAsync(json);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw new InvalidOperationException("mpv IPC 已断开", ex);
        }

        // 单条命令不该等太久；超时就放弃，避免界面卡死
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        _pending.TryRemove(id, out _);
        if (completed != tcs.Task) throw new TimeoutException("mpv 没有回应命令：" + args[0]);
        return await tcs.Task;
    }

    public Task ObservePropertyAsync(string name)
        => CommandAsync("observe_property", name.GetHashCode() & 0x7FFFFFF, name);

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                string? line = await _reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                if (line.Length < 2) continue;
                Dispatch(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        finally
        {
            FailAllPending();
            Disconnected?.Invoke();
        }
    }

    private void Dispatch(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("request_id", out var ridEl) && ridEl.TryGetInt32(out int rid))
            {
                if (!_pending.TryRemove(rid, out var tcs)) return;

                string err = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                if (err != "success")
                {
                    tcs.TrySetException(new InvalidOperationException("mpv: " + err));
                    return;
                }
                tcs.TrySetResult(root.TryGetProperty("data", out var d) ? d.Clone() : null);
                return;
            }

            if (!root.TryGetProperty("event", out var ev)) return;
            string name = ev.GetString() ?? "";

            if (name == "property-change")
            {
                string prop = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                JsonElement? data = root.TryGetProperty("data", out var dv) ? dv.Clone() : null;
                PropertyChanged?.Invoke(prop, data);
                return;
            }

            EventReceived?.Invoke(name, root);
        }
    }

    private void FailAllPending()
    {
        foreach (var kv in _pending)
        {
            kv.Value.TrySetException(new InvalidOperationException("mpv 已退出"));
            _pending.TryRemove(kv.Key, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(1)); } catch { /* 收尾不抛 */ }

        // mpv 已经退出时，往断掉的管道里 flush 会抛 IOException，收尾阶段不该冒出去
        try { _reader.Dispose(); } catch (IOException) { }
        try { _writer.Dispose(); } catch (IOException) { }
        try { _pipe.Dispose(); } catch (IOException) { }
        _cts.Dispose();
    }
}
