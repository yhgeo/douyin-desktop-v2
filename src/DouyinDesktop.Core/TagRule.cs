using DouyinDesktop.Core.Models;

namespace DouyinDesktop.Core;

/// <summary>
/// 标签过滤规则。用户在设置里写几行文本，一行一条：
///
/// <code>
/// 助眠            白名单：只要作品带这个标签就保留
/// #荒野           带不带 # 都行，会被去掉
/// -短剧           黑名单：命中就丢弃，优先级高于白名单
/// </code>
///
/// 白名单为空 = 不限制（只按黑名单过滤）。
/// 匹配是**包含**关系（"短剧"能命中"热播短剧"），不区分大小写。
///
/// 为什么用"包含"而不是精确匹配：抖音的标签很碎
/// （`#热播短剧` `#短剧推荐` `#AI短剧`），精确匹配几乎命中不了。
/// </summary>
public sealed class TagRule
{
    public List<string> Allow { get; } = new();

    public List<string> Block { get; } = new();

    /// <summary>一条规则都没有 —— 调用方可以完全跳过过滤。</summary>
    public bool IsEmpty => Allow.Count == 0 && Block.Count == 0;

    public static TagRule Parse(string? text)
    {
        var rule = new TagRule();
        if (string.IsNullOrWhiteSpace(text)) return rule;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim().TrimStart('#').Trim();
            if (line.Length == 0) continue;

            // 以 - 或 ！ 开头当黑名单（两个都认，用户手滑打错也能用）
            bool block = line[0] is '-' or '！' or '!';
            if (block) line = line[1..].Trim().TrimStart('#').Trim();

            if (line.Length == 0) continue;

            var target = block ? rule.Block : rule.Allow;
            if (!target.Contains(line, StringComparer.OrdinalIgnoreCase))
                target.Add(line);
        }

        return rule;
    }

    /// <summary>这条作品能不能留下。</summary>
    public bool Allows(Aweme item)
    {
        if (IsEmpty) return true;

        // 作者标签 + 平台标签一起参与匹配。
        // 平台标签（video_tag）只有详情接口给，所以列表过滤时通常只有作者标签；
        // 详情加载后再判一次能更准。
        var tags = item.Hashtags.Concat(item.OfficialTags).ToList();

        // 黑名单优先：带一个就丢，不管白名单
        foreach (string blocked in Block)
        {
            foreach (string tag in tags)
            {
                if (tag.Contains(blocked, StringComparison.OrdinalIgnoreCase)) return false;
            }
        }

        if (Allow.Count == 0) return true;

        foreach (string allowed in Allow)
        {
            foreach (string tag in tags)
            {
                if (tag.Contains(allowed, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }

    /// <summary>一行摘要，显示在设置页/状态栏。</summary>
    public string Describe()
    {
        if (IsEmpty) return "未设置";
        var parts = new List<string>();
        if (Allow.Count > 0) parts.Add($"只看 {Allow.Count} 个");
        if (Block.Count > 0) parts.Add($"屏蔽 {Block.Count} 个");
        return string.Join("、", parts);
    }
}
