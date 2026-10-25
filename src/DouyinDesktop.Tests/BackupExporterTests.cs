using DouyinDesktop.Core;
using DouyinDesktop.Core.Models;

namespace DouyinDesktop.Tests;

/// <summary>
/// 备份导出。
///
/// 重点验证两件容易出错的事：
///   · CSV 里的逗号、引号、换行必须正确转义，否则 Excel 打开整个错列
///   · HTML 里的文案必须转义，否则一条带 &lt;script&gt; 的标题就能破坏整个页面
/// 另外确认「永久标识」确实被写进去了——这是"以后还能找回来"的唯一依靠。
/// </summary>
public class BackupExporterTests
{
    private static Aweme Make(string id = "123", string desc = "测试文案")
        => new()
        {
            Id = id,
            Desc = desc,
            CreateTime = 1791887775,
            Author = new Author { Nickname = "作者甲", SecUid = "MS4wLjABAAAAtest", Uid = "999" },
            Statistics = new Statistics { DiggCount = 178904, CommentCount = 1522 },
            Video = new VideoInfo
            {
                Duration = 36100,
                Width = 1920,
                Height = 1080,
                PlayAddr = new PlayAddr
                {
                    Uri = "v0200fg10000abcdef",
                    UrlList = new List<string> { "https://cdn.example.com/v.mp4?token=1" },
                },
                Cover = new UrlList { Urls = new List<string> { "https://cdn.example.com/c.jpg" } },
            },
            TextExtra = new List<TextExtra> { new() { HashtagName = "游戏" } },
        };

    [Fact]
    public void 导出保留永久标识()
    {
        var item = BackupExporter.From(Make());

        Assert.Equal("123", item.AwemeId);
        Assert.Equal("v0200fg10000abcdef", item.VideoId);          // 用来重取播放地址
        Assert.Equal("https://www.douyin.com/video/123", item.WebUrl); // 作品没删就永久有效
        Assert.Equal("video", item.Type);
        Assert.Equal("作者甲", item.AuthorName);
        Assert.Equal(36100, item.DurationMs);
        Assert.Contains("游戏", item.Hashtags);
    }

    [Fact]
    public void 图集被标记为image()
    {
        var withImages = new Aweme
        {
            Id = "456",
            Desc = "图集",
            Author = new Author { Nickname = "作者乙" },
            Images = new List<ImageInfo>
            {
                new() { UrlList = new List<string> { "https://cdn.example.com/1.jpg" } },
            },
        };

        var item = BackupExporter.From(withImages);

        Assert.Equal("image", item.Type);
        Assert.Single(item.Images);
    }

    [Fact]
    public void CSV转义逗号引号换行()
    {
        var aweme = Make(desc: "含逗号, 含\"引号\" 还有\n换行");
        var file = BackupExporter.Build(new[] { aweme }, "collect");
        string csv = BackupExporter.ToCsv(file);

        // 带 BOM，Excel 才不把中文显示成乱码
        Assert.StartsWith("\uFEFF", csv);

        // 整段必须被引号包住，内部引号翻倍
        Assert.Contains("\"含逗号, 含\"\"引号\"\" 还有\n换行\"", csv);
    }

    [Fact]
    public void CSV表头包含找回视频的关键字段()
    {
        string csv = BackupExporter.ToCsv(BackupExporter.Build(new[] { Make() }, "collect"));
        string header = csv.Split('\n')[0];

        Assert.Contains("作品ID", header);
        Assert.Contains("网页链接", header);
        Assert.Contains("视频ID", header);
    }

    [Fact]
    public void HTML转义危险字符()
    {
        var aweme = Make(desc: "<script>alert(1)</script> & \"引号\"");
        var file = BackupExporter.Build(new[] { aweme }, "collect");
        string html = BackupExporter.ToHtml(file);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
    }

    [Fact]
    public void HTML里每张卡片都指向原视频()
    {
        var items = new[] { Make("111"), Make("222") };
        string html = BackupExporter.ToHtml(BackupExporter.Build(items, "collect"));

        Assert.Contains("https://www.douyin.com/video/111", html);
        Assert.Contains("https://www.douyin.com/video/222", html);
        Assert.Contains("target=\"_blank\"", html);
    }

    [Fact]
    public void JSON能读回来()
    {
        var file = BackupExporter.Build(new[] { Make() }, "collect");
        string json = BackupExporter.ToJson(file);

        var back = System.Text.Json.JsonSerializer.Deserialize<BackupFile>(json);

        Assert.NotNull(back);
        Assert.Equal(1, back!.Count);
        Assert.Equal("123", back.Items[0].AwemeId);
        // 中文没有被转义成 \uXXXX
        Assert.Contains("测试文案", json);
    }

    [Fact]
    public void 空集合也能导出()
    {
        var file = BackupExporter.Build(Array.Empty<Aweme>(), "collect");

        Assert.Equal(0, file.Count);
        Assert.Empty(file.Items);
        Assert.Contains("0 条", BackupExporter.ToHtml(file));
    }
}
