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
    public sealed record ActionDef(string Id, string Label, Key Default, Key[] Aliases,
        bool Basic = false);

    public static readonly ActionDef[] Actions =
    {
        // Basic = 太基础了，详情页的快捷键提示里不用列（设置里照样能改）
        new("prev",       "上一条",       Key.Up,     new[] { Key.W, Key.PageUp }, true),
        new("next",       "下一条",       Key.Down,   new[] { Key.S, Key.PageDown }, true),
        new("pause",      "暂停 / 继续",  Key.Space,  Array.Empty<Key>(), true),
        new("seekBack",   "快退 5 秒",    Key.Left,   new[] { Key.A }, true),
        new("seekForward","快进 5 秒",    Key.Right,  new[] { Key.D }, true),
        new("mute",       "静音",         Key.M,      Array.Empty<Key>(), true),
        new("fullscreen", "全屏",         Key.F,      Array.Empty<Key>(), true),
        new("tab",        "详情 / 评论 / 主页", Key.Tab, Array.Empty<Key>()),
        new("like",       "点赞 / 取消",  Key.X,      Array.Empty<Key>()),
        new("openweb",    "在浏览器中打开", Key.B,    Array.Empty<Key>()),
        new("follow",     "关注页 / 推荐页", Key.G,   Array.Empty<Key>()),
        new("taggroup",   "切换标签分组",   Key.None, Array.Empty<Key>()),

        // 下面三个**默认不绑键**：都不是高频操作，绑着反而容易误触。
        // 要用的自己去 设置 → 快捷键 里加。
        //   · 回推荐：Esc 已经能做（全屏时先退全屏），不需要单独的键
        //   · 换一批：翻到快见底时会自动补，不必手动刷
        //   · 登录：设置 → 账号 里有扫码入口
        new("refresh",    "换一批推荐",   Key.None,   Array.Empty<Key>()),
        new("login",      "登录",         Key.None,   Array.Empty<Key>()),
        new("author",     "作者主页 / 回推荐", Key.None, Array.Empty<Key>()),
    };

    private readonly Dictionary<string, Key> _keys = new();

    // 配置版本。v2 起「作者主页 / 换一批 / 登录」默认不绑键。
    // 老配置里存着旧默认（U / R / L，或者用户自己改过的别的键），
    // 留着会让人觉得"改了没生效"，所以升级时统一清一次，之后就按用户的选择来。
    private const string VersionKey = "__v";
    private const string CurrentVersion = "2";

    private static readonly HashSet<string> UnboundInV2 = new() { "refresh", "login", "author" };

    public event Action? Changed;

    public static string DefaultPath => DouyinDesktop.Core.DouyinPaths.SettingsFile;

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
        if (key == Key.None) return null;

        // 第一轮只看用户绑定的主键。
        // 自定义必须优先于内置别名——否则把「点赞」改成 W 之后，
        // W 会先被排在数组前面的「上一条」别名吃掉，用户改的键永远不生效。
        foreach (var a in Actions)
        {
            if (Get(a.Id) == key) return a.Id;
        }

        // 第二轮才轮到别名（WASD / PageUp / PageDown 这些固定别名）
        foreach (var a in Actions)
        {
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

            bool legacy = !saved.TryGetValue(VersionKey, out string? ver) || ver != CurrentVersion;

            foreach (var (id, name) in saved)
            {
                if (id == VersionKey) continue;

                // 老配置：这三个键的旧绑定直接丢掉，落到新默认（未设置）
                if (legacy && UnboundInV2.Contains(id)) continue;

                if (Enum.TryParse<Key>(name, out var key)) map._keys[id] = key;
            }

            if (legacy) map.Save();     // 迁移只做一次
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
            data[VersionKey] = CurrentVersion;
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
