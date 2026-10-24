using System.Text;

namespace DouyinDesktop.Core;

/// <summary>
/// 标签分组：把不同的过滤规则存成几套，快速切换。
///
/// 文本格式（INI 风格，好手写也好改）：
/// <code>
/// [助眠]
/// 助眠
/// 解压
/// -短剧
///
/// [技术]
/// 单片机
/// 嵌入式
/// </code>
///
/// - 一行 `[名字]` 开始一个新组
/// - 组内每行一条规则，语法同 <see cref="TagRule"/>
/// - 第一组之前如果写了规则，自动归到"默认"组
/// - 没有 `[` 的纯规则文本 = 只有一个"默认"组（兼容旧配置）
/// </summary>
public sealed class TagGroups
{
    public const string DefaultName = "默认";

    public List<Group> Items { get; } = new();

    public sealed record Group(string Name, string Rules);

    public bool IsEmpty => Items.Count == 0;

    public static TagGroups Parse(string? text)
    {
        var groups = new TagGroups();
        if (string.IsNullOrWhiteSpace(text)) return groups;

        var names = new List<string>();
        var bodies = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        string current = DefaultName;
        names.Add(current);
        bodies[current] = new StringBuilder();

        foreach (string rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = rawLine.Trim();

            // [组名]
            if (line.Length >= 2 && line[0] == '[' && line[^1] == ']')
            {
                string name = line[1..^1].Trim();
                if (name.Length == 0) continue;

                if (!bodies.ContainsKey(name))
                {
                    names.Add(name);
                    bodies[name] = new StringBuilder();
                }
                current = name;
                continue;
            }

            bodies[current].AppendLine(rawLine);
        }

        foreach (string name in names)
        {
            string body = bodies[name].ToString().Trim();

            // 只有"隐式的默认组"为空时才跳过（纯空文本不该造出一个组）。
            // **显式写了 [组名] 的组，即使规则为空也要保留** —— 否则用户新建的空分组
            // 一保存就消失，看起来像"分组被吞了"。
            if (name == DefaultName && body.Length == 0) continue;

            groups.Items.Add(new Group(name, body));
        }

        return groups;
    }

    /// <summary>取某个组的规则文本；组不存在就返回空（= 不过滤）。</summary>
    public string RulesOf(string? name)
    {
        if (Items.Count == 0) return "";

        if (!string.IsNullOrEmpty(name))
        {
            foreach (var g in Items)
                if (string.Equals(g.Name, name, StringComparison.Ordinal)) return g.Rules;
        }

        return Items[0].Rules;
    }

    /// <summary>循环切到下一个组，返回组名；只有一个组时原样返回。</summary>
    public string Next(string? name)
    {
        if (Items.Count == 0) return "";
        if (Items.Count == 1) return Items[0].Name;

        int idx = Items.FindIndex(g => string.Equals(g.Name, name, StringComparison.Ordinal));
        return Items[(idx + 1) % Items.Count].Name;
    }

    /// <summary>状态栏用：`助眠（只看 2 个）`。</summary>
    public string Describe(string? name)
    {
        if (Items.Count == 0) return "未设置";

        // 找出实际生效的组名（名字对不上时退回第一个）
        string effective = Items[0].Name;
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var g in Items)
            {
                if (!string.Equals(g.Name, name, StringComparison.Ordinal)) continue;
                effective = g.Name;
                break;
            }
        }

        var rule = TagRule.Parse(RulesOf(name));
        return rule.IsEmpty ? $"{effective}（不过滤）" : $"{effective}（{rule.Describe()}）";
    }
}
