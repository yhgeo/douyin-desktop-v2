using DouyinDesktop.Core.Api;

// 数据层验证探针：只依赖 Core，不写任何硬编码凭据。
// 跑法（项目根目录）：dotnet run --project src/DouyinDesktop.Probe
//
// 判定标准：能自己拿到凭据 → 能取到推荐流 → 能换出可播放的 MP4 字节。
//
// 评论接口单独验证：dotnet run --project src/DouyinDesktop.Probe -- comment

if (args.Length > 0 && args[0] == "comment")
{
    using var cc = DouyinClient.Create();
    cc.Log += m => Console.WriteLine("    " + m);

    var f = await cc.GetFeedAsync(5);
    if (!f.Success) { Console.WriteLine("拉推荐失败：" + f.Message); return 1; }

    var target = f.Items.FirstOrDefault(x => (x.Statistics?.CommentCount ?? 0) > 0);
    if (target is null) { Console.WriteLine("这批里没有带评论的作品"); return 1; }

    Console.WriteLine($"作品 {target.Id}  @{target.AuthorName}  评论数 {target.Statistics?.CommentCount}");
    var page = await cc.GetCommentsAsync(target.Id);
    Console.WriteLine($"返回 {page.Comments.Count} 条 / total={page.Total} / hasMore={page.HasMore}\n");

    foreach (var cm in page.Comments.Take(8))
        Console.WriteLine($"  {cm.Nickname}: {cm.Text}   ♥{cm.DiggCount}  [{cm.MetaText}]");
    return 0;
}

// 图集作品验证：dotnet run --project src/DouyinDesktop.Probe -- images
if (args.Length > 0 && args[0] == "images")
{
    using var ci = DouyinClient.Create();
    ci.Log += m => Console.WriteLine("    " + m);

    var seen = new List<string>();
    var all = new List<DouyinDesktop.Core.Models.Aweme>();
    for (int page = 0; page < 6; page++)
    {
        var p = await ci.GetFeedAsync(10, seen);
        if (!p.Success) { Console.WriteLine("拉取失败：" + p.Message); break; }
        seen.AddRange(p.Items.Select(x => x.Id));
        all.AddRange(p.Items);

        foreach (var a in p.Items)
        {
            if (!a.IsImagePost) continue;
            Console.WriteLine($"图集 {a.Id}  @{a.AuthorName}  {a.Images!.Count} 张  {a.Desc}");
            foreach (var img in a.Images.Take(3))
                Console.WriteLine($"    {img.Width}×{img.Height}  {img.Url[..Math.Min(80, img.Url.Length)]}");
        }
    }

    Console.WriteLine($"\n共扫 {all.Count} 条");
    Console.WriteLine($"  images 字段存在：{all.Count(x => x.Images is not null)}");
    Console.WriteLine($"  images 非空    ：{all.Count(x => x.Images is { Count: > 0 })}");
    Console.WriteLine($"  有视频 uri     ：{all.Count(x => x.IsPlayable)}");
    Console.WriteLine($"  既无图也无视频 ：{all.Count(x => !x.IsPlayable && !x.IsImagePost)}");
    return 0;
}

using var client = DouyinClient.Create();
client.Log += msg => Console.WriteLine("    " + msg);

Console.WriteLine("[1] 获取凭据");
var session = await client.EnsureSessionAsync();
Console.WriteLine($"    ttwid      {(session.Ttwid.Length > 0 ? session.Ttwid[..Math.Min(32, session.Ttwid.Length)] + "…" : "(空)")}");
Console.WriteLine($"    s_v_web_id {session.SvWebId}");
Console.WriteLine($"    msToken    {(session.MsToken.Length > 0 ? session.MsToken[..Math.Min(20, session.MsToken.Length)] + "…" : "(空，服务端不校验)")}");

Console.WriteLine("\n[2] 拉推荐流");
var feed = await client.GetFeedAsync(10);
if (!feed.Success)
{
    Console.WriteLine("    失败：" + feed.Message);
    return 1;
}
Console.WriteLine($"    成功 {feed.Items.Count} 条 / {feed.ElapsedMs}ms\n");

foreach (var a in feed.Items.Take(5))
{
    Console.WriteLine($"    {a.Id}  {a.DurationText}  ♥{a.Statistics?.DiggCount ?? 0}  " +
                      $"@{a.AuthorName}  {Short(a.Desc, 26)}");
}

var first = feed.Items.FirstOrDefault(x => x.IsPlayable);
if (first is null)
{
    Console.WriteLine("\n[3] 这批里没有可播视频（可能全是图集）");
    return 0;
}

Console.WriteLine($"\n[3] 换播放地址（{first.Id}）");
string playUrl = DouyinClient.BuildPlayUrl(first.Video!.PlayAddr!.Uri);
Console.WriteLine("    " + playUrl);

var probe = await client.ProbeStreamAsync(first.Video.PlayAddr.Uri);
Console.WriteLine($"    {probe.Detail}  {probe.ElapsedMs}ms  ok={probe.Ok}");

// 翻页去重：拿已展示的 id 再要一批，确认没有重复
Console.WriteLine("\n[4] 翻页去重");
var next = await client.GetFeedAsync(10, feed.Items.Select(x => x.Id));
if (next.Success)
{
    var overlap = next.Items.Select(x => x.Id).Intersect(feed.Items.Select(x => x.Id)).Count();
    Console.WriteLine($"    新一批 {next.Items.Count} 条，与上一批重复 {overlap} 条 / {next.ElapsedMs}ms");
}
else
{
    Console.WriteLine("    失败：" + next.Message);
}

return probe.Ok ? 0 : 1;

static string Short(string s, int max)
    => string.IsNullOrEmpty(s) ? "(无文案)" : s.Length <= max ? s : s[..max] + "…";
