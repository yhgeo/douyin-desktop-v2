using DouyinDesktop.Core.Api;
using DouyinDesktop.Core.Models;

namespace DouyinDesktop.Tests;

/// <summary>
/// 作品解析的容错。
///
/// 这几条全部来自真实踩坑（2026-10-26），每条都对应一次"界面打不开"：
///   · `text_extra` 数组里有 null 元素 → 标签过滤炸 NullReference → 收藏整页打不开
///   · `is_commerce` 有时是 0/1 有时是 true/false → 定义成 bool 会让整条作品解析失败
///   · 喜欢/收藏返回 `aweme_list`，不是推荐流的 `cards` → 只认一种就解析出 0 条
/// </summary>
public class AwemeParsingTests
{
    [Fact]
    public void 话题名为JSONnull时不炸()
    {
        // `hashtag_name: null` 会把属性默认值 "" 覆盖成 null，
        // 只判 `x is not null` 是不够的（这是第二次踩同一个坑的原因）
        const string json = """
        {"aweme_id":"1","text_extra":[{"hashtag_name":null},{"hashtag_name":"美食"}]}
        """;

        var item = DouyinClient.ParseOne(json);

        Assert.NotNull(item);
        Assert.Equal(new[] { "美食" }, item!.Hashtags);
    }

    [Fact]
    public void 平台标签名为JSONnull时不炸()
    {
        const string json = """
        {"aweme_id":"1","video_tag":[{"tag_name":null},{"tag_name":"游戏"}]}
        """;

        var item = DouyinClient.ParseOne(json);

        Assert.NotNull(item);
        Assert.Equal(new[] { "游戏" }, item!.OfficialTags);
    }

    [Fact]
    public void 标签过滤碰到脏数据不炸()
    {
        // 这是真实的崩溃路径：TagRule.Allows → item.Hashtags
        var items = new[]
        {
            new Aweme
            {
                Id = "1",
                TextExtra = new List<TextExtra> { null!, new() { HashtagName = null! }, new() { HashtagName = "美食" } },
            },
            new Aweme { Id = "2", TextExtra = null },
        };

        var rule = DouyinDesktop.Core.TagRule.Parse("美食");

        Assert.True(rule.Allows(items[0]));
        Assert.False(rule.Allows(items[1]));
    }

    [Fact]
    public void 话题数组里有null元素时不炸()
    {
        var item = new Aweme
        {
            Id = "1",
            TextExtra = new List<TextExtra>
            {
                null!,                                    // ← 服务端真的会这么给
                new() { HashtagName = "美食" },
                new() { HashtagName = "" },               // 空名也要跳过
            },
        };

        Assert.Equal(new[] { "美食" }, item.Hashtags);
    }

    [Fact]
    public void 平台标签数组里有null元素时不炸()
    {
        var item = new Aweme
        {
            Id = "1",
            VideoTag = new List<VideoTag>
            {
                null!,
                new() { TagName = "游戏" },
            },
        };

        Assert.Equal(new[] { "游戏" }, item.OfficialTags);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("\"1\"", true)]
    [InlineData("\"true\"", true)]
    [InlineData("null", false)]
    public void is_commerce各种类型都能解析(string raw, bool expected)
    {
        string json = $$"""
        {"aweme_id":"1","desc":"x","text_extra":[{"hashtag_name":"t","is_commerce":{{raw}}}]}
        """;

        var item = DouyinClient.ParseOne(json);

        Assert.NotNull(item);
        Assert.NotNull(item!.TextExtra);
        Assert.Equal(expected, item.TextExtra![0].IsCommerce);
    }

    [Fact]
    public void 解析喜欢与收藏的aweme_list结构()
    {
        // 收藏接口的元素是**对象**；有些接口给的是 JSON 字符串。两种都要认。
        const string json = """
        {
          "status_code": 0,
          "aweme_list": [
            {"aweme_id":"111","desc":"第一条","author":{"nickname":"甲"}},
            "{\"aweme_id\":\"222\",\"desc\":\"第二条\",\"author\":{\"nickname\":\"乙\"}}"
          ]
        }
        """;

        var items = DouyinClient.ParseFeed(json);

        Assert.Equal(2, items.Count);
        Assert.Equal("111", items[0].Id);
        Assert.Equal("222", items[1].Id);
        Assert.Equal("乙", items[1].AuthorName);
    }

    [Fact]
    public void 解析推荐流的cards结构()
    {
        const string json = """
        {
          "cards": [
            {"aweme": "{\"aweme_id\":\"333\",\"desc\":\"卡片\"}"},
            {"card_type": 1}
          ]
        }
        """;

        var items = DouyinClient.ParseFeed(json);

        Assert.Single(items);
        Assert.Equal("333", items[0].Id);
    }

    [Fact]
    public void 坏数据不影响同页其它条目()
    {
        // 第二条的 aweme_id 给了个对象，类型对不上必然失败；第一条和第三条要保住。
        // （is_commerce 给对象不会失败——宽松转换器会跳过，这是有意的）
        const string json = """
        {
          "aweme_list": [
            {"aweme_id":"1","desc":"好"},
            {"aweme_id":{"bad":1}},
            {"aweme_id":"3","desc":"也好"}
          ]
        }
        """;

        var items = DouyinClient.ParseFeed(json);

        Assert.Equal(2, items.Count);
        Assert.Equal(new[] { "1", "3" }, items.Select(x => x.Id));
    }
}
