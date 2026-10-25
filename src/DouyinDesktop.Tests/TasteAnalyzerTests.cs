using DouyinDesktop.Core;
using DouyinDesktop.Core.Models;

namespace DouyinDesktop.Tests;

/// <summary>
/// 推荐倾向分析。
///
/// 这里盯的是三个容易悄悄出错的地方：
///   · 占比分母用的是"已归类条数"，不是总条数（否则分布会被压扁）
///   · 平台活动标签（#XX计划）不能混进主题
///   · 一条作品只归一个主题，各主题加起来不超过已归类总数
/// </summary>
public class TasteAnalyzerTests
{
    private static Aweme Make(string id, string author, params string[] tags)
        => new()
        {
            Id = id,
            Author = new Author { Nickname = author, SecUid = "MS4wLjABAAAA" + id },
            TextExtra = tags.Select(t => new TextExtra { HashtagName = t }).ToList(),
        };

    [Fact]
    public void 按主题归类_短剧和游戏分开()
    {
        var items = new[]
        {
            Make("1", "甲", "热播短剧", "追剧"),
            Make("2", "乙", "三角洲行动", "游戏日常"),
            Make("3", "丙", "美食", "探店"),
        };

        var rep = TasteAnalyzer.Analyze(items);

        Assert.Equal(3, rep.Sampled);
        Assert.Equal(3, rep.WithTags);
        Assert.Equal(3, rep.Classified);

        var themes = rep.Themes.Select(t => t.Theme).ToList();
        Assert.Contains("短剧/影视", themes);
        Assert.Contains("游戏", themes);
        Assert.Contains("美食", themes);
    }

    [Fact]
    public void 占比分母是已归类条数_不是总条数()
    {
        // 2 条能归类（短剧），2 条完全没标签
        var items = new[]
        {
            Make("1", "甲", "短剧"),
            Make("2", "乙", "短剧推荐"),
            Make("3", "丙"),
            Make("4", "丁"),
        };

        var rep = TasteAnalyzer.Analyze(items);

        Assert.Equal(4, rep.Sampled);
        Assert.Equal(2, rep.Classified);

        var theme = Assert.Single(rep.Themes);
        Assert.Equal("短剧/影视", theme.Theme);
        // 2/2 = 100%，而不是 2/4 = 50%
        Assert.Equal(1.0, theme.Percent, 3);
    }

    [Fact]
    public void 平台活动标签不算主题()
    {
        // 只有活动标签，没有内容标签 → 归不了类
        var items = new[]
        {
            Make("1", "甲", "抖音精选计划"),
            Make("2", "乙", "AI创作浪潮计划"),
        };

        var rep = TasteAnalyzer.Analyze(items);

        Assert.Equal(0, rep.Classified);
        Assert.Empty(rep.Themes);
        Assert.Equal(2, rep.CampaignTagCount);
        Assert.Contains("平台活动标签", rep.Summary);
    }

    [Fact]
    public void 活动标签和内容标签并存时_按内容标签归类()
    {
        var items = new[]
        {
            Make("1", "甲", "抖音精选计划", "短剧"),
            Make("2", "乙", "创作者成长计划", "游戏"),
        };

        var rep = TasteAnalyzer.Analyze(items);

        Assert.Equal(2, rep.Classified);
        Assert.Equal(2, rep.CampaignTagCount);

        var themes = rep.Themes.Select(t => t.Theme).ToList();
        Assert.Contains("短剧/影视", themes);
        Assert.Contains("游戏", themes);
        // 活动标签不该成为主题名
        Assert.DoesNotContain("抖音精选计划", themes);
    }

    [Fact]
    public void 作者集中度超过三成会在结论里点名()
    {
        var items = Enumerable.Range(0, 10)
            .Select(i => Make(i.ToString(), i < 5 ? "刷屏号" : "别人", "游戏"))
            .ToArray();

        var rep = TasteAnalyzer.Analyze(items);

        Assert.Equal(5, rep.TopAuthors[0].Count);
        Assert.Contains("刷屏号", rep.Summary);
        Assert.Contains("高度集中", rep.Summary);
    }

    [Fact]
    public void 空输入不炸()
    {
        var rep = TasteAnalyzer.Analyze(Array.Empty<Aweme>());

        Assert.Equal(0, rep.Sampled);
        Assert.Empty(rep.Themes);
        Assert.Equal(0, rep.TagCoverage);
        Assert.False(string.IsNullOrWhiteSpace(rep.Summary));
    }

    [Fact]
    public void 完全没标签时给出可读结论()
    {
        var items = new[] { Make("1", "甲"), Make("2", "乙") };

        var rep = TasteAnalyzer.Analyze(items);

        Assert.Equal(0, rep.WithTags);
        Assert.Contains("一条标签都没有", rep.Summary);
    }
}
