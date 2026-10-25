using DouyinDesktop.Core.Models;

namespace DouyinDesktop.Core;

/// <summary>一个标签出现了多少次。</summary>
public sealed record TagCount(string Tag, int Count);

/// <summary>一个主题下有多少条作品、占比多少、代表标签是什么。</summary>
public sealed record ThemeCount(string Theme, int Count, double Percent, IReadOnlyList<string> Samples);

/// <summary>一个作者贡献了几条。</summary>
public sealed record AuthorCount(string Name, int Count);

/// <summary>
/// 「当前推荐倾向」的分析结果。
///
/// 数据全部来自推荐流本身（<c>text_extra</c> 话题标签），不额外请求、
/// 不猜用户画像——所以它反映的是**服务端此刻正在推什么**，
/// 而不是"用户是什么人"。两者接近但不相等，文案上别混。
/// </summary>
public sealed class TasteReport
{
    public int Sampled { get; init; }
    public int WithTags { get; init; }
    public int TagTotal { get; init; }
    public int ImagePosts { get; init; }

    /// <summary>能被主题归类的作品数（分母用这个算占比）。</summary>
    public int Classified { get; init; }

    /// <summary>拿到平台标签（video_tag）的作品数。只有逐条取详情才有。</summary>
    public int OfficialCovered { get; init; }

    /// <summary>命中平台运营标签的次数（#XX计划 这类）。</summary>
    public int CampaignTagCount { get; init; }

    public IReadOnlyList<TagCount> TopTags { get; init; } = Array.Empty<TagCount>();
    public IReadOnlyList<ThemeCount> Themes { get; init; } = Array.Empty<ThemeCount>();
    public IReadOnlyList<AuthorCount> TopAuthors { get; init; } = Array.Empty<AuthorCount>();
    public string Summary { get; init; } = "";

    /// <summary>带标签的比例（0–1）。</summary>
    public double TagCoverage => Sampled == 0 ? 0 : (double)WithTags / Sampled;
}

/// <summary>
/// 把一批推荐作品归纳成"推荐倾向"。
///
/// 做法刻意保守：
///   · 只用话题标签，不去解析文案（文案里抓关键词误报率高得多）
///   · 主题按**作品数**算占比（一条作品只归一个主题），所以各主题加起来是 100%
///   · 标签词表用"包含"匹配，因为抖音标签很碎（#热播短剧 / #短剧推荐 / #AI短剧）
///
/// 纯函数，不联网、不读磁盘，方便单测。
/// </summary>
public static class TasteAnalyzer
{
    /// <summary>
    /// 主题词表。顺序有意义：一个标签只归**第一个**匹配上的主题，
    /// 所以"游戏解说"会算进"短剧/影视"（因为"解说"在前面），
    /// 想让某类更优先就把它的关键词往前挪。
    /// </summary>
    private static readonly (string Theme, string[] Keys)[] ThemeMap =
    {
        ("短剧/影视", new[]
        {
            "短剧", "剧场", "追剧", "剧集", "电视剧", "电影", "影视", "解说", "漫剧",
            "动漫", "动画", "纪录片", "综艺", "预告", "混剪", "片段", "精讲", "重温",
            "经典", "配音", "大结局",
        }),
        ("游戏", new[]
        {
            "游戏", "王者", "荣耀", "原神", "和平精英", "我的世界", "steam", "电竞",
            "手游", "网游", "蛋仔", "英雄联盟", "lol", "主机", "单机", "通关", "对局",
            "三角洲", "使命召唤", "csgo", "dota", "apex", "pubg", "星露谷", "我的世界",
            "开黑", "排位",
        }),
        ("助眠/解压", new[]
        {
            "助眠", "解压", "asmr", "白噪音", "放松", "治愈系", "催眠", "冥想",
        }),
        ("知识科普", new[]
        {
            "科普", "知识", "历史", "数码", "科技", "编程", "单片机", "嵌入式",
            "学习", "考试", "考研", "英语", "物理", "数学", "化学", "医学", "财经",
            "商业", "职场", "军事", "法律", "涨知识", "冷知识",
        }),
        ("美食", new[]
        {
            "美食", "做饭", "菜谱", "餐厅", "小吃", "探店", "零食", "烘焙", "吃播",
            "夜宵", "火锅", "家常菜", "印度美食",
        }),
        ("搞笑娱乐", new[]
        {
            "搞笑", "沙雕", "段子", "整活", "喜剧", "幽默", "神回复", "名场面", "梗",
            "mrbeast", "野兽先生", "恶搞", "挑战",
        }),
        ("音乐舞蹈", new[]
        {
            "音乐", "翻唱", "舞蹈", "dj", "歌曲", "演奏", "钢琴", "吉他", "说唱",
            "编曲", "纯音乐",
        }),
        ("萌宠动物", new[]
        {
            "猫", "狗", "宠物", "萌宠", "动物", "喵", "汪", "野生动物",
        }),
        ("运动健身", new[]
        {
            "健身", "运动", "篮球", "足球", "跑步", "减肥", "瑜伽", "体育", "乒乓",
            "羽毛球", "游泳", "骑行",
        }),
        ("情感", new[]
        {
            "情感", "恋爱", "婚姻", "两性", "心理", "emo", "分手", "相亲",
        }),
        ("旅行风景", new[]
        {
            "旅行", "风景", "旅游", "自驾", "露营", "户外", "航拍", "徒步", "雪山", "海边",
            "环球",
        }),
        ("穿搭美妆", new[]
        {
            "穿搭", "美妆", "化妆", "护肤", "时尚", "发型", "ootd", "美甲", "香水",
        }),
        ("汽车", new[]
        {
            "汽车", "改装", "新能源", "试驾", "越野", "机车", "摩托车",
        }),
        ("母婴育儿", new[]
        {
            "育儿", "母婴", "宝宝", "怀孕", "亲子", "辅食", "早教",
        }),
        ("荒野/建造", new[]
        {
            "荒野", "建造", "野外", "生存", "树屋", "盖房", "求生", "末世", "末日",
            "丧尸", "废土",
        }),
        ("生活日常", new[]
        {
            "vlog", "日常", "生活", "记录", "随手拍", "家庭", "带娃", "农村", "乡村",
            "改造", "手工", "收纳", "装修", "精神小妹",
        }),
    };

    /// <summary>没匹配上任何主题时归到这里，让占比仍然凑满 100%。</summary>
    public const string OtherTheme = "其他";

    /// <summary>
    /// 平台运营标签：`#抖音精选计划` `#AI创作浪潮计划` `#OiiOii创作者计划` 这类。
    /// 它们是活动位，不代表内容类型——混进主题统计会把分布搅乱
    /// （第一版没排除，结果"其他"占了 40%）。
    /// </summary>
    private static bool IsPlatformCampaign(string tag)
        => tag.Contains("计划", StringComparison.Ordinal)
        || tag.Contains("创作者", StringComparison.Ordinal)
        || tag.Contains("挑战赛", StringComparison.Ordinal)
        || tag.Contains("官方", StringComparison.Ordinal);

    public static TasteReport Analyze(IEnumerable<Aweme> items, int topTags = 18, int topThemes = 8)
    {
        var list = items.ToList();

        var tagCounter = new Dictionary<string, int>(StringComparer.Ordinal);
        var themeCounter = new Dictionary<string, int>(StringComparer.Ordinal);
        var themeSamples = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var authorCounter = new Dictionary<string, int>(StringComparer.Ordinal);

        int withTags = 0, tagTotal = 0, imagePosts = 0, campaignTags = 0, officialCovered = 0;

        foreach (var item in list)
        {
            if (item.IsImagePost) imagePosts++;

            // 作者标签 + 平台标签一起看：平台标签更准，但覆盖率低，先并起来
            var tags = item.Hashtags.Concat(item.OfficialTags)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (tags.Count > 0) withTags++;
            tagTotal += tags.Count;

            foreach (string tag in tags)
            {
                tagCounter[tag] = tagCounter.GetValueOrDefault(tag) + 1;
                if (IsPlatformCampaign(tag)) campaignTags++;
            }

            // 主题判定：**平台标签优先**。
            //
            // 平台标签是抖音自己的三级分类（"美食 / 美食探店 / 餐厅探店"），
            // 每个 tag_name 是分开的一项，第一项就是一级分类，比手工词表准得多。
            // 只有拿不到平台标签（推荐流里没有、详情失败）时才退回作者标签 + 词表。
            var official = item.OfficialTags;
            bool hasOfficial = official.Count > 0;
            if (hasOfficial) officialCovered++;

            string theme = OtherTheme;
            if (hasOfficial)
            {
                string top = official[0].Split('/')[0].Trim();
                if (top.Length > 0 && !IsPlatformCampaign(top)) theme = top;
            }

            if (theme == OtherTheme)
            {
                // 一条作品只归一个主题：取第一个能匹配上的标签。
                // 平台运营标签跳过——"#抖音精选计划"说明不了这是哪类内容。
                foreach (string tag in tags)
                {
                    if (IsPlatformCampaign(tag)) continue;
                    string? hit = MatchTheme(tag);
                    if (hit is not null) { theme = hit; break; }
                }
            }

            themeCounter[theme] = themeCounter.GetValueOrDefault(theme) + 1;
            if (!themeSamples.TryGetValue(theme, out var bag))
            {
                bag = new Dictionary<string, int>(StringComparer.Ordinal);
                themeSamples[theme] = bag;
            }
            foreach (string tag in tags) bag[tag] = bag.GetValueOrDefault(tag) + 1;

            string author = item.AuthorName;
            authorCounter[author] = authorCounter.GetValueOrDefault(author) + 1;
        }

        int sampled = list.Count;

        // 占比按"已归类"算，不把"其他"算进分母——
        // 否则一个词表覆盖不到的主题会把所有真实主题的百分比压扁，看着像"没什么倾向"。
        int classified = themeCounter.Where(x => x.Key != OtherTheme).Sum(x => x.Value);

        var themes = themeCounter
            .Where(x => x.Key != OtherTheme)
            .OrderByDescending(x => x.Value)
            .Take(topThemes)
            .Select(x => new ThemeCount(
                x.Key,
                x.Value,
                classified == 0 ? 0 : (double)x.Value / classified,
                themeSamples.TryGetValue(x.Key, out var bag)
                    ? bag.Where(y => !IsPlatformCampaign(y.Key))
                         .OrderByDescending(y => y.Value).Take(3).Select(y => y.Key).ToList()
                    : new List<string>()))
            .ToList();

        var topTagList = tagCounter
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(topTags)
            .Select(x => new TagCount(x.Key, x.Value))
            .ToList();

        var authors = authorCounter
            .OrderByDescending(x => x.Value)
            .Take(5)
            .Select(x => new AuthorCount(x.Key, x.Value))
            .ToList();

        return new TasteReport
        {
            Sampled = sampled,
            WithTags = withTags,
            TagTotal = tagTotal,
            ImagePosts = imagePosts,
            Classified = classified,
            OfficialCovered = officialCovered,
            CampaignTagCount = campaignTags,
            TopTags = topTagList,
            Themes = themes,
            TopAuthors = authors,
            Summary = BuildSummary(sampled, withTags, classified, officialCovered, campaignTags, themes, authors),
        };
    }

    /// <summary>标签属于哪个主题；没命中返回 null。</summary>
    private static string? MatchTheme(string tag)
    {
        foreach (var (theme, keys) in ThemeMap)
        {
            foreach (string key in keys)
            {
                // 太短的英文词（如 ai）容易误伤，要求整词等于
                if (key.Length <= 2 && key.All(char.IsAsciiLetter))
                {
                    if (string.Equals(tag, key, StringComparison.OrdinalIgnoreCase)) return theme;
                    continue;
                }

                if (tag.Contains(key, StringComparison.OrdinalIgnoreCase)) return theme;
            }
        }
        return null;
    }

    private static string BuildSummary(
        int sampled, int withTags, int classified, int officialCovered, int campaignTags,
        IReadOnlyList<ThemeCount> themes, IReadOnlyList<AuthorCount> authors)
    {
        if (sampled == 0) return "没采到数据。";

        if (withTags == 0)
            return $"采到 {sampled} 条，但一条标签都没有——这批内容没法归类。";

        var parts = new List<string>();

        var named = themes.Take(3).ToList();
        if (named.Count == 0)
        {
            parts.Add("标签都对不上已知主题，可能是词表需要补");
        }
        else
        {
            string joined = string.Join("、", named.Select(t => $"{t.Theme} {t.Percent:P0}"));
            string source = officialCovered >= classified / 2 ? "按平台分类" : "按作者标签";
            parts.Add($"能归类的 {classified} 条里（{source}），最集中的是 {joined}");
        }

        // 作者集中度：一个号占三成以上就值得提醒（说明这一屏其实是一个人刷屏）
        if (authors.Count > 0 && sampled > 0)
        {
            var top = authors[0];
            double share = (double)top.Count / sampled;
            if (share >= 0.3)
                parts.Add($"但内容高度集中，仅「{top.Name}」就占 {share:P0}");
        }

        if (campaignTags > 0)
            parts.Add($"另有 {campaignTags} 次平台活动标签（#XX计划 之类）未计入主题");

        return string.Join("；", parts) + "。";
    }
}
