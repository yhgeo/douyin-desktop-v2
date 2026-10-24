using DouyinDesktop.Core;
using DouyinDesktop.Core.Models;

namespace DouyinDesktop.Tests;

/// <summary>
/// 标签过滤。规则是"一行一条，- 前缀表示屏蔽"，匹配用包含关系。
/// 这些行为一旦被改错，用户会觉得"我设了过滤但没生效"，很难查。
/// </summary>
public class TagRuleTests
{
    private static Aweme Make(params string[] tags) => new()
    {
        Id = "1",
        TextExtra = tags.Select(t => new TextExtra { HashtagName = t }).ToList(),
    };

    [Fact]
    public void 空规则放行全部()
    {
        var rule = TagRule.Parse("");
        Assert.True(rule.IsEmpty);
        Assert.True(rule.Allows(Make("随便")));
        Assert.True(rule.Allows(Make()));
    }

    [Fact]
    public void 白名单命中就保留()
    {
        var rule = TagRule.Parse("助眠");
        Assert.True(rule.Allows(Make("助眠", "户外")));
        Assert.False(rule.Allows(Make("短剧")));
    }

    [Fact]
    public void 黑名单命中就丢弃()
    {
        var rule = TagRule.Parse("-短剧");
        Assert.False(rule.Allows(Make("热播短剧")));
        Assert.True(rule.Allows(Make("助眠")));
    }

    [Fact]
    public void 黑名单优先于白名单()
    {
        // 同时命中"助眠"和"短剧"，应该丢
        var rule = TagRule.Parse("助眠\n-短剧");
        Assert.False(rule.Allows(Make("助眠", "热播短剧")));
        Assert.True(rule.Allows(Make("助眠")));
    }

    [Fact]
    public void 带井号也能解析()
    {
        var rule = TagRule.Parse("#助眠\n-#短剧");
        Assert.Single(rule.Allow);
        Assert.Single(rule.Block);
        Assert.Equal("助眠", rule.Allow[0]);
        Assert.Equal("短剧", rule.Block[0]);
    }

    [Fact]
    public void 包含匹配而不是精确匹配()
    {
        // 抖音标签很碎：#热播短剧 #短剧推荐 #AI短剧，精确匹配几乎命中不了
        var rule = TagRule.Parse("-短剧");
        Assert.False(rule.Allows(Make("短剧推荐")));
        Assert.False(rule.Allows(Make("AI短剧")));
        Assert.False(rule.Allows(Make("热播短剧")));
    }

    [Fact]
    public void 不区分大小写()
    {
        var rule = TagRule.Parse("-AI");
        Assert.False(rule.Allows(Make("ai创作")));
        Assert.False(rule.Allows(Make("Ai绘画")));
    }

    [Fact]
    public void 无标签的作品在白名单下会被丢弃()
    {
        var rule = TagRule.Parse("助眠");
        Assert.False(rule.Allows(Make()));      // 没标签，没法判断，宁可丢

        var onlyBlock = TagRule.Parse("-短剧");
        Assert.True(onlyBlock.Allows(Make()));  // 只有黑名单时，无标签的照留
    }

    [Fact]
    public void 重复规则会去重()
    {
        var rule = TagRule.Parse("助眠\n助眠\n#助眠");
        Assert.Single(rule.Allow);
    }

    [Fact]
    public void 空行和纯空白会被忽略()
    {
        var rule = TagRule.Parse("\n\n  \n助眠\n\n-\n");
        Assert.Single(rule.Allow);
        Assert.Empty(rule.Block);       // 只有一个 - 的行不算规则
    }

    [Fact]
    public void 摘要文案()
    {
        Assert.Equal("未设置", TagRule.Parse("").Describe());
        Assert.Equal("只看 1 个", TagRule.Parse("助眠").Describe());
        Assert.Equal("屏蔽 2 个", TagRule.Parse("-短剧\n-游戏").Describe());
        Assert.Equal("只看 1 个、屏蔽 1 个", TagRule.Parse("助眠\n-短剧").Describe());
    }
}
