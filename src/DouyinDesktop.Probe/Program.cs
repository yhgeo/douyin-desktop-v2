using DouyinDesktop.Core;
using DouyinDesktop.Core.Api;

// 数据层验证探针：只依赖 Core，不写任何硬编码凭据。
// 跑法（项目根目录）：dotnet run --project src/DouyinDesktop.Probe
//
// 判定标准：能自己拿到凭据 → 能取到推荐流 → 能换出可播放的 MP4 字节。
//
// 评论接口单独验证：dotnet run --project src/DouyinDesktop.Probe -- comment

// 磁盘预取缓存的离线自测：dotnet run --project src/DouyinDesktop.Probe -- cache
// 不联网、不弹窗，只验证读写 / 换流拒绝 / 容量清理
if (args.Length > 0 && args[0] == "cache")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    string dir = Path.Combine(Path.GetTempPath(), "dy-prefetch-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    Console.WriteLine($"临时目录 {dir}");

    var cache = PrefetchCache.TryOpen(dir, maxBytes: 3 * 1024 * 1024, maxEntries: 5);
    if (cache is null) { Console.WriteLine("TryOpen 返回 null，失败"); return 1; }

    const string url = "https://v3-web.douyinvod.com/abc/video.mp4?a=1&u=token1";
    byte[] data = new byte[1024 * 1024];
    for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);

    cache.Save("111", url, data, 20_000_000, "video/mp4");
    bool ok = cache.TryLoad("111", url, out var got);
    Console.WriteLine($"[1] 读回={ok} 长度={got.Data.Length} 字节一致={got.Data.AsSpan().SequenceEqual(data)} 总长={got.TotalLength}");

    bool ok2 = cache.TryLoad("111", "https://other-cdn.douyinvod.com/abc/video.mp4?u=token2", out _);
    Console.WriteLine($"[2] 同路径换 host/query -> {ok2}（期望 True）");

    bool ok3 = cache.TryLoad("111", "https://v3-web.douyinvod.com/xyz/other.mp4?u=token1", out _);
    Console.WriteLine($"[3] 换路径（换了流）-> {ok3}（期望 False）");

    for (int i = 0; i < 10; i++) cache.Save($"9{i:00}", url, data, 1000, "video/mp4");
    cache.Trim();
    var (count, bytes) = cache.Stats();
    Console.WriteLine($"[4] 清理后 {count} 条 / {bytes / 1024.0 / 1024.0:0.0} MB（上限 3MB、5 条）");

    Console.WriteLine($"[5] 不存在的 id -> {cache.TryLoad("nope", url, out _)}（期望 False）");

    Directory.Delete(dir, true);
    Console.WriteLine("已清理临时目录");
    return 0;
}

// 「接着上次看」的现场存取自测：dotnet run --project src/DouyinDesktop.Probe -- session
// 拉一批真实数据，存盘再读回，逐字段比对
if (args.Length > 0 && args[0] == "session")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var cs = DouyinClient.Create();
    cs.Log += m => Console.WriteLine("    " + m);

    var f = await cs.GetFeedAsync(5);
    if (!f.Success) { Console.WriteLine("拉推荐失败：" + f.Message); return 1; }

    string tmp = Path.Combine(Path.GetTempPath(), "dy-session-" + Guid.NewGuid().ToString("N")[..8] + ".json");
    LastSession.Save(2, f.Items, tmp);

    var loaded = LastSession.Load(path: tmp);
    if (loaded is null) { Console.WriteLine("读回失败"); File.Delete(tmp); return 1; }

    Console.WriteLine($"存 {f.Items.Count} 条 -> 读回 {loaded.Items.Count} 条，index={loaded.Index}");
    int same = 0;
    for (int i = 0; i < Math.Min(5, loaded.Items.Count); i++)
    {
        var a = loaded.Items[i];
        var b = f.Items[i];
        bool ok = a.Id == b.Id
                  && a.Desc == b.Desc
                  && a.Video?.PlayAddr?.Uri == b.Video?.PlayAddr?.Uri
                  && a.Author?.Nickname == b.Author?.Nickname
                  && a.Statistics?.DiggCount == b.Statistics?.DiggCount
                  && a.IsImagePost == b.IsImagePost
                  && a.Music?.Title == b.Music?.Title;
        if (ok) same++;
        Console.WriteLine($"  [{i}] 一致={ok} id={a.Id} 作者={a.Author?.Nickname} 播放地址长度={a.Video?.PlayAddr?.Uri?.Length ?? 0}");
    }
    Console.WriteLine($"逐字段一致 {same}/{Math.Min(5, loaded.Items.Count)}");
    Console.WriteLine($"文件 {new FileInfo(tmp).Length / 1024} KB");
    File.Delete(tmp);
    return same == Math.Min(5, loaded.Items.Count) ? 0 : 1;
}

// 评论接口验证：登录态下是否还返回 0 条
if (args.Length > 0 && args[0] == "comments")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var cd = DouyinClient.Create();
    cd.Log += m => Console.WriteLine("    " + m);

    var f = await cd.GetFeedAsync(5);
    if (!f.Success) { Console.WriteLine("拉推荐失败：" + f.Message); return 1; }

    var item = f.Items[0];
    string targetId = args.Length > 1 ? args[1] : item.Id;
    Console.WriteLine($"作品 {targetId} 评论数 {item.Statistics?.CommentCount}");
    var page = await cd.GetCommentsAsync(targetId, 0, 20);
    Console.WriteLine($"拿到 {page.Comments.Count} 条 / 共 {page.Total}");
    foreach (var c in page.Comments.Take(2)) Console.WriteLine("    · " + c.Text);
    return 0;
}

// 点赞接口试探：用不存在的作品 id，不会在账号上留下真实点赞，
// 只看请求形状对不对（签名/方法/参数名）。
if (args.Length > 0 && args[0] == "diggtest")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var cd = DouyinClient.Create();
    cd.Log += m => Console.WriteLine("    " + m);

    Console.WriteLine("用假 id 试点赞：");
    await cd.DiggAsync("7000000000000000000", true);
    Console.WriteLine("用假 id 试取消：");
    await cd.DiggAsync("7000000000000000000", false);
    return 0;
}

// 弹幕接口试探：dotnet run --project src/DouyinDesktop.Probe -- danmaku
// 路径是抓包确认的，参数还在猜——先看返回什么
if (args.Length > 0 && args[0] == "danmaku")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var cd = DouyinClient.Create();
    cd.Log += m => Console.WriteLine("    " + m);

    var f = await cd.GetFeedAsync(5);
    if (!f.Success) { Console.WriteLine("拉推荐失败：" + f.Message); return 1; }

    foreach (var item in f.Items.Take(3))
    {
        long dur = item.Video?.Duration ?? 0;
        Console.WriteLine($"\n作品 {item.Id}  时长 {dur}ms");

        var p1 = await cd.GetDanmakuAsync(item.Id, 0, 32000);
        Console.WriteLine($"  窗 0-32s -> start={p1.StartMs} end={p1.EndMs} {p1.Items.Count} 条");
        foreach (var d in p1.Items.Take(3))
            Console.WriteLine($"     {d.OffsetMs}ms  {d.Text}");

        var p2 = await cd.GetDanmakuAsync(item.Id, 32000, 64000);
        Console.WriteLine($"  窗 32-64s -> start={p2.StartMs} end={p2.EndMs} {p2.Items.Count} 条");

        if (p1.Items.Count > 0)
        {
            string ass = DouyinDesktop.Core.Models.DanmakuAss.Build(
                p1.Items, item.Video?.Width ?? 1080, item.Video?.Height ?? 1920);
            var lines = ass.Split('\n');
            Console.WriteLine("  Script Info：");
            foreach (var ln in lines.Take(5)) Console.WriteLine("    " + ln.TrimEnd());
            Console.WriteLine("  Style: " + lines.First(x => x.StartsWith("Style:")).TrimEnd());
            var dlgs = lines.Where(x => x.StartsWith("Dialogue:")).ToList();
            Console.WriteLine($"  Dialogue 行数 = {dlgs.Count}");
            foreach (var ln in dlgs.Take(6)) Console.WriteLine("    " + ln.TrimEnd());

            string outPath = Path.Combine(Path.GetTempPath(), "dm-test.ass");
            File.WriteAllText(outPath, ass);
            Console.WriteLine("  ASS 已写到 " + outPath);
            break;      // 一个作品就够了
        }
    }
    return 0;
}

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
            Console.WriteLine($"    music = '{a.Music?.Title}' / '{a.Music?.Author}'");
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

// 作者主页作品（图集的唯一来源）：dotnet run --project src/DouyinDesktop.Probe -- user [sec_uid]
if (args.Length > 0 && args[0] == "user")
{
    using var cu = DouyinClient.Create();
    cu.Log += m => Console.WriteLine("    " + m);

    // 指定了 sec_uid 就只查这一个，并打印每条作品的序号和类型
    if (args.Length > 1)
    {
        var one = await cu.GetUserPostsAsync(args[1]);
        if (!one.Success) { Console.WriteLine("失败：" + one.Message); return 1; }

        Console.WriteLine($"作品 {one.Items.Count} 条\n");
        for (int i = 0; i < one.Items.Count; i++)
        {
            var p = one.Items[i];
            string kind = p.IsImagePost ? $"图集 {p.Images!.Count} 张" : "视频";
            string desc = p.Desc.Length > 26 ? p.Desc[..26] : p.Desc;
            Console.WriteLine($"  #{i + 1,2}  {kind,-10}  {desc}");
        }
        return 0;
    }

    var authors = new Dictionary<string, string>();   // sec_uid -> nickname
    for (int round = 0; round < 3 && authors.Count < 6; round++)
    {
        var f = await cu.GetFeedAsync(10);
        if (!f.Success) continue;
        foreach (var a in f.Items)
        {
            var au = a.Author;
            if (au is null || string.IsNullOrEmpty(au.SecUid)) continue;
            authors.TryAdd(au.SecUid, au.Nickname);
            if (authors.Count >= 6) break;
        }
    }

    Console.WriteLine($"准备查 {authors.Count} 个作者的主页\n");

    int totalImages = 0;
    foreach (var (secUid, nickname) in authors)
    {
        var posts = await cu.GetUserPostsAsync(secUid);
        if (!posts.Success)
        {
            Console.WriteLine($"@{nickname,-14} 失败：{posts.Message}");
            continue;
        }

        int imageCount = posts.Items.Count(x => x.IsImagePost);
        totalImages += imageCount;
        string types = string.Join(",", posts.Items.GroupBy(x => x.AwemeType)
            .Select(g => $"{g.Key}×{g.Count()}"));
        Console.WriteLine($"@{nickname,-14} 作品 {posts.Items.Count,2} 条  图集 {imageCount}  " +
                          $"aweme_type: {types}  sec_uid={secUid}");

        foreach (var p in posts.Items.Where(x => x.IsImagePost).Take(3))
        {
            string desc = p.Desc.Length > 24 ? p.Desc[..24] : p.Desc;
            Console.WriteLine($"    图集 {p.Id}  {p.Images!.Count} 张  {desc}");
            Console.WriteLine($"         首图 {p.Images[0].Url[..Math.Min(70, p.Images[0].Url.Length)]}");
        }
    }

    Console.WriteLine($"\n合计图集 {totalImages} 条");
    return 0;
}

// 推荐流参数实验：dotnet run --project src/DouyinDesktop.Probe -- feedtest
// 目标：找到能让推荐流返回图集（aweme_type=68）的参数组合
if (args.Length > 0 && args[0] == "feedtest")
{
    string[] modules = { "3003101", "3003100", "3003102", "" };

    foreach (string moduleId in modules)
    {
        var profile = new DeviceProfile { ModuleId = moduleId };
        using var c = DouyinClient.Create(profile);

        var seen = new List<string>();
        int total = 0, imageCount = 0;

        for (int i = 0; i < 3; i++)
        {
            var r = await c.GetFeedAsync(20, seen);
            if (!r.Success) { Console.WriteLine($"  module={moduleId} 失败：{r.Message}"); break; }
            seen.AddRange(r.Items.Select(x => x.Id));
            total += r.Items.Count;
            imageCount += r.Items.Count(x => x.IsImagePost);
        }

        string label = string.IsNullOrEmpty(moduleId) ? "(不带)" : moduleId;
        Console.WriteLine($"module_id={label,-8} 共 {total,3} 条  图集 {imageCount}");
    }
    return 0;
}

// 首页 tab 流实验：dotnet run --project src/DouyinDesktop.Probe -- tabtest
if (args.Length > 0 && args[0] == "tabtest")
{
    using var ct2 = DouyinClient.Create();
    ct2.Log += m => Console.WriteLine("    " + m);

    int total = 0, imageCount = 0;
    for (int i = 0; i < 3; i++)
    {
        var r = await ct2.GetTabFeedAsync(20);
        if (!r.Success) { Console.WriteLine("失败：" + r.Message); break; }
        total += r.Items.Count;
        imageCount += r.Items.Count(x => x.IsImagePost);
        string types = string.Join(",", r.Items.GroupBy(x => x.AwemeType).Select(g => $"{g.Key}×{g.Count()}"));
        Console.WriteLine($"  第 {i + 1} 批 {r.Items.Count} 条  aweme_type: {types}");
    }
    Console.WriteLine($"\n共 {total} 条，图集 {imageCount}");
    return 0;
}

// 评论回复接口验证：dotnet run --project src/DouyinDesktop.Probe -- replies
if (args.Length > 0 && args[0] == "replies")
{
    using var cr = DouyinClient.Create();
    cr.Log += m => Console.WriteLine("    " + m);

    var f = await cr.GetFeedAsync(5);
    var target = f.Items.FirstOrDefault(x => (x.Statistics?.CommentCount ?? 0) > 0);
    if (target is null) { Console.WriteLine("没找到带评论的作品"); return 1; }

    var page = await cr.GetCommentsAsync(target.Id);
    var withReplies = page.Comments.FirstOrDefault(x => x.HasReplies);
    if (withReplies is null) { Console.WriteLine("这批评论都没有回复"); return 1; }

    Console.WriteLine($"评论「{withReplies.Text[..Math.Min(30, withReplies.Text.Length)]}」" +
                      $"有 {withReplies.ReplyCount} 条回复\n");

    var replies = await cr.GetCommentRepliesAsync(target.Id, withReplies.Id);
    Console.WriteLine($"拿到 {replies.Comments.Count} 条回复 / hasMore={replies.HasMore}\n");
    foreach (var r in replies.Comments.Take(6))
        Console.WriteLine($"  {r.Nickname}: {r.Text}   [{r.MetaText}]");
    return 0;
}

// 滚动加载版推荐流：dotnet run --project src/DouyinDesktop.Probe -- scroll
if (args.Length > 0 && args[0] == "scroll")
{
    using var cs = DouyinClient.Create();
    cs.Log += m => Console.WriteLine("    " + m);

    int total = 0, imageCount = 0;
    for (int i = 0; i < 4; i++)
    {
        var r = await cs.GetFeedScrollAsync(10, i + 2);
        if (!r.Success) { Console.WriteLine("失败：" + r.Message); break; }

        total += r.Items.Count;
        imageCount += r.Items.Count(x => x.IsImagePost);
        string types = string.Join(",", r.Items.GroupBy(x => x.AwemeType)
            .Select(g => $"{g.Key}×{g.Count()}"));
        Console.WriteLine($"  第 {i + 1} 批 {r.Items.Count} 条  aweme_type: {types}");
        foreach (var x in r.Items.Take(2))
            Console.WriteLine($"     ip_label='{x.IpLabel}'  @{x.AuthorName}");

        foreach (var img in r.Items.Where(x => x.IsImagePost))
            Console.WriteLine($"    图集 {img.Id}  {img.Images!.Count} 张  {img.Desc[..Math.Min(24, img.Desc.Length)]}");
    }
    Console.WriteLine($"\n共 {total} 条，图集 {imageCount}");
    return 0;
}

// 详情接口（IP 属地）：dotnet run --project src/DouyinDesktop.Probe -- detail
if (args.Length > 0 && args[0] == "detail")
{
    using var cd = DouyinClient.Create();
    cd.Log += m => Console.WriteLine("    " + m);

    var f = await cd.GetFeedAsync(3);
    if (!f.Success) { Console.WriteLine("拉推荐失败：" + f.Message); return 1; }

    foreach (var it in f.Items.Take(2))
    {
        var d = await cd.GetAwemeDetailAsync(it.Id);
        Console.WriteLine($"  {it.Id}  ip='{d?.IpLabel}'  列表里 region 为空  desc='{(d is null ? "(失败)" : d.Desc[..Math.Min(20, d.Desc.Length)])}'");
    }
    return 0;
}

// 关注流：dotnet run --project src/DouyinDesktop.Probe -- follow
if (args.Length > 0 && args[0] == "follow")
{
    using var cf = DouyinClient.Create();
    cf.Log += m => Console.WriteLine("    " + m);

    foreach (string ep in new[] { "follow/feed", "tab/feed" })
    {
        var r = await cf.GetFeedScrollAsync(10, 2, ep);
        Console.WriteLine($"  {ep}: " + (r.Success ? $"{r.Items.Count} 条" : r.Message));
        await Task.Delay(800);
    }
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
