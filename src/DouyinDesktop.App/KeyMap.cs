using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace DouyinDesktop.App;

/// <summary>
/// 快捷键映射。主键可改，WASD / 方向键作为固定别名保留——
/// 别名不让改，避免用户把上下左右改没了之后没法切视频。
/// </summary>
public sealed class KeyMap
{
    public sealed record ActionDef(string Id, string Label, Key Default, Key[] Aliases);

    public static readonly ActionDef[] Actions =
    {
        new("prev",       "上一条",       Key.Up,     new[] { Key.W, Key.PageUp }),
        new("next",       "下一条",       Key.Down,   new[] { Key.S, Key.PageDown }),
        new("pause",      "暂停 / 继续",  Key.Space,  Array.Empty<Key>()),
        new("seekBack",   "快退 5 秒",    Key.Left,   new[] { Key.A }),
        new("seekForward","快进 5 秒",    Key.Right,  new[] { Key.D }),
        new("mute",       "静音",         Key.M,      Array.Empty<Key>()),
        new("fullscreen", "全屏",         Key.F,      Array.Empty<Key>()),
        new("refresh",    "换一批推荐",   Key.R,      Array.Empty<Key>()),
        new("login",      "登录",         Key.L,      Array.Empty<Key>()),
        new("tab",        "详情 / 评论",  Key.Tab,    Array.Empty<Key>()),
        new("author",     "看作者主页",   Key.U,      Array.Empty<Key>()),
    };

    private readonly Dictionary<string, Key> _keys = new();

    public event Action? Changed;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DouyinDesktop", "settings.json");

    public KeyMap()
    {
        foreach (var a in Actions) _keys[a.Id] = a.Default;
    }

    public Key Get(string id)
        => _keys.TryGetValue(id, out var k) ? k : Key.None;

    public static ActionDef? Def(string id) => Actions.FirstOrDefault(a => a.Id == id);

    /// <summary>改主键。如果这个键已被别的动作占用，先解除那边的占用。</summary>
    public void Set(string id, Key key)
    {
        foreach (string other in _keys.Where(kv => kv.Value == key && kv.Key != id)
                     .Select(kv => kv.Key).ToList())
        {
            _keys[other] = Key.None;
        }

        _keys[id] = key;
        Save();
        Changed?.Invoke();
    }

    public void ResetAll()
    {
        foreach (var a in Actions) _keys[a.Id] = a.Default;
        Save();
        Changed?.Invoke();
    }

    /// <summary>这个按键对应哪个动作（主键或别名）。</summary>
    public string? ActionFor(Key key)
    {
        foreach (var a in Actions)
        {
            if (Get(a.Id) == key) return a.Id;
            if (a.Aliases.Contains(key)) return a.Id;
        }
        return null;
    }

    // ================= 落盘 =================

    public static KeyMap Load()
    {
        var map = new KeyMap();
        try
        {
            if (!File.Exists(DefaultPath)) return map;

            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(DefaultPath));
            if (saved is null) return map;

            foreach (var (id, name) in saved)
            {
                if (Enum.TryParse<Key>(name, out var key)) map._keys[id] = key;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // 配置坏了就用默认值，不要让程序起不来
        }
        return map;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
            var data = _keys.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
            File.WriteAllText(DefaultPath, JsonSerializer.Serialize(data,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // 存不下不影响本次使用
        }
    }

    /// <summary>给界面看的键名（中文习惯的叫法）。</summary>
    public static string KeyText(Key key) => key switch
    {
        Key.None => "（未设置）",
        Key.Space => "空格",
        Key.Tab => "Tab",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Left => "←",
        Key.Right => "→",
        Key.Return => "回车",
        Key.Escape => "Esc",
        _ => key.ToString(),
    };
}
