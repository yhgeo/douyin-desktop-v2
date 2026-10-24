using DouyinDesktop.Core;

namespace DouyinDesktop.Tests;

/// <summary>
/// 标签分组的解析与切换。用户手写这段文本，格式容错必须够宽——
/// 少个空行、多打一个 `[`、组名前后有空格，都不能把规则搞丢。
/// </summary>
public class TagGroupsTests
{
    [Fact]
    public void 空文本没有任何组()
    {
        var g = TagGroups.Parse("");
        Assert.True(g.IsEmpty);
        Assert.Equal("", g.RulesOf(null));
        Assert.Equal("未设置", g.Describe(null));
    }

    [Fact]
    public void 纯规则文本当成默认组()
    {
        var g = TagGroups.Parse("助眠\n-短剧");
        Assert.Single(g.Items);
        Assert.Equal("默认", g.Items[0].Name);
        Assert.Contains("助眠", g.Items[0].Rules);
    }

    [Fact]
    public void 多组分别解析()
    {
        var g = TagGroups.Parse("[助眠]\n助眠\n解压\n\n[技术]\n单片机\n-短剧");

        Assert.Equal(2, g.Items.Count);
        Assert.Equal("助眠", g.Items[0].Name);
        Assert.Equal("技术", g.Items[1].Name);
        Assert.Contains("单片机", g.RulesOf("技术"));
        Assert.DoesNotContain("单片机", g.RulesOf("助眠"));
    }

    [Fact]
    public void 组名前后空格会被去掉()
    {
        var g = TagGroups.Parse("[ 助眠 ]\n助眠");
        Assert.Single(g.Items);
        Assert.Equal("助眠", g.Items[0].Name);
    }

    [Fact]
    public void 循环切换()
    {
        var g = TagGroups.Parse("[A]\na\n[B]\nb\n[C]\nc");

        Assert.Equal("B", g.Next("A"));
        Assert.Equal("C", g.Next("B"));
        Assert.Equal("A", g.Next("C"));       // 转回来
        Assert.Equal("A", g.Next("不存在"));   // 名字对不上 → 从头开始
        Assert.Equal("A", g.Next(null));
    }

    [Fact]
    public void 只有一个组时切换不动()
    {
        var g = TagGroups.Parse("[A]\na");
        Assert.Equal("A", g.Next("A"));
    }

    [Fact]
    public void 组名不存在时退回第一组()
    {
        var g = TagGroups.Parse("[A]\na\n[B]\nb");

        Assert.Equal("b", g.RulesOf("B"));          // 有这一组，取它自己的
        Assert.Equal("a", g.RulesOf("不存在"));     // 没有就退回第一组
    }

    [Fact]
    public void 摘要会带上生效的组名()
    {
        var g = TagGroups.Parse("[助眠]\n助眠\n解压\n[技术]\n-短剧");

        Assert.Contains("助眠", g.Describe("助眠"));
        Assert.Contains("只看 2 个", g.Describe("助眠"));
        Assert.Contains("技术", g.Describe("技术"));
        Assert.Contains("屏蔽 1 个", g.Describe("技术"));
    }

    [Fact]
    public void 空组会被忽略()
    {
        // 只有一个空的 [X]，应该什么都不剩
        var g = TagGroups.Parse("[X]\n\n");
        Assert.True(g.IsEmpty);
    }

    [Fact]
    public void 兼容CRLF换行()
    {
        var g = TagGroups.Parse("[助眠]\r\n助眠\r\n[技术]\r\n单片机");
        Assert.Equal(2, g.Items.Count);
        Assert.Contains("单片机", g.RulesOf("技术"));
    }

    [Fact]
    public void 分组规则能直接喂给TagRule()
    {
        var g = TagGroups.Parse("[助眠]\n助眠\n-短剧");
        var rule = TagRule.Parse(g.RulesOf("助眠"));

        Assert.False(rule.IsEmpty);
        Assert.Equal("只看 1 个、屏蔽 1 个", rule.Describe());
    }
}
