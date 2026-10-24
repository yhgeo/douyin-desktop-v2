using System.IO;
using System.Text.Json;
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

// 详情接口自检：看返回里有没有 user_digged（点赞回读校验要用）
if (args.Length > 0 && args[0] == "detail")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var cd = DouyinClient.Create();
    cd.Log += m => Console.WriteLine("    " + m);

    var f = await cd.GetFeedAsync(5);
    if (!f.Success) { Console.WriteLine("拉推荐失败：" + f.Message); return 1; }

    string id = args.Length > 1 ? args[1] : f.Items[0].Id;
    var item = await cd.GetAwemeDetailAsync(id);
    Console.WriteLine(item is null ? "详情为空" : $"详情 ok：{item.Desc[..Math.Min(20, item.Desc.Length)]}");
    return 0;
}

// 签名对齐测试：用固定时间/固定随机数算，和参考实现逐字节比对
if (args.Length > 0 && args[0] == "sign")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    string q = args.Length > 1
        ? args[1]
        : "device_platform=webapp&aid=6383&channel=channel_pc_web&aweme_id=7698704627360943375&cursor=0&count=5&item_type=0&whale_cut_token=&cut_version=1&rcFT=";

    var s = new DouyinDesktop.Core.Signing.ABogus(true, fixedNowMs: 1720000000000L, fixedCounter: 2);
    Console.WriteLine("QUERY=" + q);
    Console.WriteLine("ABOGUS=" + s.SignQuery(q, "", "www.douyin.com"));
    return 0;
}

// 连续多次评论请求：看第几次开始失败，并打印每次的完整 URL 供对比
if (args.Length > 0 && args[0] == "comments3")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var cd = DouyinClient.Create();
    cd.Log += m => Console.WriteLine("    " + m);

    var f = await cd.GetFeedAsync(6);
    if (!f.Success) { Console.WriteLine("拉推荐失败：" + f.Message); return 1; }

    // 让探针也能用接口模板（和主程序一致）
    var tplPath = Path.Combine(DouyinDesktop.Core.DouyinPaths.DataDirectory, "api-templates.json");
    if (File.Exists(tplPath))
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(tplPath));
        if (map is not null) cd.GetTemplate = p => map.TryGetValue(p, out var u) ? u : null;
        Console.WriteLine("TPL=" + string.Join(",", map is null ? new List<string>() : map.Keys.ToList()));
    }
    else Console.WriteLine("TPL=none  DIR=" + DouyinDesktop.Core.DouyinPaths.DataDirectory);

    string? tok3 = Environment.GetEnvironmentVariable("DY_MSTOKEN");
    if (tok3 is not null)
    {
        await cd.EnsureSessionAsync();
        if (cd.Session is not null)
        {
            cd.Session.MsToken = tok3 == "EMPTY" ? "" : tok3;
            Console.WriteLine("MSTOKEN_LEN=" + cd.Session.MsToken.Length);
        }
    }

    for (int i = 0; i < Math.Min(4, f.Items.Count); i++)
    {
        string id = f.Items[i].Id;
        Console.WriteLine($"--- 第 {i + 1} 次：{id} ---");
        var page = await cd.GetCommentsAsync(id, 0, 5);
        Console.WriteLine($"    结果：{page.Comments.Count} 条");
        await Task.Delay(1500);
    }
    return 0;
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

    // 加 token 参数就替换 msToken，用来验证"签名没问题、只是 token 不对"
    // DY_MSTOKEN=EMPTY 表示置空（刚登录时就是这个状态）
    // 必须先 EnsureSessionAsync —— 否则 Session 还是 null，赋值会静默跳过
    string? envToken = Environment.GetEnvironmentVariable("DY_MSTOKEN");
    if (envToken is not null)
    {
        await cd.EnsureSessionAsync();
        if (cd.Session is not null)
        {
            cd.Session.MsToken = envToken == "EMPTY" ? "" : envToken;
            Console.WriteLine($"（已替换 msToken，长度 {cd.Session.MsToken.Length}）");
        }
    }

    // 加 guest 参数就清掉登录 Cookie，用来对比"游客 vs 登录"两种状态下评论接口的表现
    if (args.Contains("guest") && cd.Session is not null)
    {
        cd.Session.LoginCookies = "";
        Console.WriteLine("（已清空登录 Cookie，按游客态请求）");
    }
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

// 点赞端到端：真实点赞一条 → 回读 → 取消 → 回读（净效果为零）
// dotnet run --project src/DouyinDesktop.Probe -- diggsync
if (args.Length > 0 && args[0] == "diggsync")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    // 有抓到的 secsdk 头就带上（浏览器真实值），和主程序一致
    string secPath = Path.Combine(DouyinPaths.DataDirectory, "secsdk-headers.json");
    if (File.Exists(secPath))
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(secPath));
        if (map is not null) { c.GetExtraHeaders = () => map; Console.WriteLine($"secsdk 头: {string.Join(",", map.Keys)}"); }
    }
    else Console.WriteLine("secsdk 头: 没有（用 Cookie 里的 csrf 兜底）");

    var f = await c.GetFeedAsync(10);
    if (!f.Success) { Console.WriteLine("拉推荐失败：" + f.Message); return 1; }

    var target = f.Items.FirstOrDefault(x => x.UserDigged == 0);
    if (target is null) { Console.WriteLine("这批里没有未点赞的作品"); return 1; }

    Console.WriteLine($"目标 {target.Id}  @{target.AuthorName}  点赞数 {target.Statistics?.DiggCount}");

    async Task Sample(string tag)
    {
        for (int i = 1; i <= 3; i++)
        {
            await Task.Delay(1500);
            var d = await c.GetAwemeDetailAsync(target.Id);
            Console.WriteLine($"    {tag} +{i * 1.5:0.0}s  user_digged={d?.UserDigged}  count={d?.Statistics?.DiggCount}");
        }
    }

    Console.WriteLine("\n[0] 基线");
    var baseD = await c.GetAwemeDetailAsync(target.Id);
    Console.WriteLine($"    user_digged={baseD?.UserDigged}  count={baseD?.Statistics?.DiggCount}");

    Console.WriteLine("\n[1] 点赞");
    Console.WriteLine($"    请求返回 {await c.DiggAsync(target.Id, true)}");
    await Sample("赞后");

    Console.WriteLine("\n[2] 取消");
    Console.WriteLine($"    请求返回 {await c.DiggAsync(target.Id, false)}");
    await Sample("取消后");

    Console.WriteLine("\n[3] 重新拉一批，看列表里的状态");
    var f2 = await c.GetFeedAsync(10);
    var again = f2.Items.FirstOrDefault(x => x.Id == target.Id);
    Console.WriteLine(again is null
        ? "    （这条不在新一批里，无法对比）"
        : $"    列表 user_digged={again.UserDigged}");
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

    // 必须和主程序一样挂上接口模板。
    // 不挂的话走的是"自己拼参数"的老路，实测大概率空 body，
    // 会让人误以为登录态失效了（实际只是模板没生效）。
    AttachTemplates(cc);

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

// 标签覆盖率：dotnet run --project src/DouyinDesktop.Probe -- tags
// 判断"按标签做自定义推荐"可不可行——先看有多少作品带标签。
if (args.Length > 0 && args[0] == "tags")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    var counter = new Dictionary<string, int>(StringComparer.Ordinal);
    int total = 0, withTag = 0, tagSum = 0;

    for (int page = 0; page < 2; page++)
    {
        string body = await c.GetFeedRawAsync(10);
        if (body.Length == 0) { Console.WriteLine("空 body"); break; }

        var nodes = new List<JsonElement>();
        using (var doc = JsonDocument.Parse(body))
        {
            if (doc.RootElement.TryGetProperty("aweme_list", out var al) && al.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in al.EnumerateArray()) nodes.Add(a.Clone());
            }
            else if (doc.RootElement.TryGetProperty("cards", out var cards) && cards.ValueKind == JsonValueKind.Array)
            {
                foreach (var card in cards.EnumerateArray())
                {
                    if (!card.TryGetProperty("aweme", out var aw)) continue;
                    if (aw.ValueKind == JsonValueKind.String)
                    {
                        try { using var inner = JsonDocument.Parse(aw.GetString()!); nodes.Add(inner.RootElement.Clone()); }
                        catch (JsonException) { }
                    }
                    else if (aw.ValueKind == JsonValueKind.Object) nodes.Add(aw.Clone());
                }
            }
        }

        foreach (var aw in nodes)
        {
            total++;
            var tags = new List<string>();
            if (aw.TryGetProperty("text_extra", out var te) && te.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in te.EnumerateArray())
                {
                    if (t.TryGetProperty("hashtag_name", out var h) && h.ValueKind == JsonValueKind.String)
                    {
                        string name = h.GetString() ?? "";
                        if (name.Length > 0) tags.Add(name);
                    }
                }
            }

            if (tags.Count > 0) withTag++;
            tagSum += tags.Count;
            foreach (string t in tags) counter[t] = counter.GetValueOrDefault(t) + 1;

            string nick = "";
            if (aw.TryGetProperty("author", out var au) && au.TryGetProperty("nickname", out var n) && n.ValueKind == JsonValueKind.String)
                nick = n.GetString() ?? "";

            Console.WriteLine($"  @{nick,-14} {(tags.Count == 0 ? "(无标签)" : string.Join(" ", tags.Select(x => "#" + x)))}");
        }
        await Task.Delay(1200);
    }

    Console.WriteLine($"\n共 {total} 条，带标签 {withTag} 条（{(total == 0 ? 0 : withTag * 100 / total)}%），" +
                      $"平均每条 {(total == 0 ? 0 : tagSum / total)} 个标签");
    Console.WriteLine("\n出现最多的标签：");
    foreach (var (name, n) in counter.OrderByDescending(x => x.Value).Take(12))
        Console.WriteLine($"    {n,2}  #{name}");
    return 0;
}

// 作品有哪些字段：dotnet run --project src/DouyinDesktop.Probe -- fields
// 回答"推荐视频有没有标签"——模型里没有的字段只能看原始 JSON。
if (args.Length > 0 && args[0] == "fields")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    string body = await c.GetFeedRawAsync(6);
    Console.WriteLine($"原始长度 {body.Length}");
    if (body.Length == 0) { Console.WriteLine("空 body"); return 1; }

    using var doc = JsonDocument.Parse(body);

    JsonElement? sample = null;
    if (doc.RootElement.TryGetProperty("aweme_list", out var al) && al.ValueKind == JsonValueKind.Array)
        sample = al.EnumerateArray().FirstOrDefault();
    else if (doc.RootElement.TryGetProperty("cards", out var cards) && cards.ValueKind == JsonValueKind.Array)
    {
        foreach (var card in cards.EnumerateArray())
        {
            if (!card.TryGetProperty("aweme", out var aw)) continue;
            if (aw.ValueKind == JsonValueKind.String)
                sample = JsonDocument.Parse(aw.GetString()!).RootElement;
            else if (aw.ValueKind == JsonValueKind.Object)
                sample = aw;
            if (sample is not null) break;
        }
    }

    if (sample is null) { Console.WriteLine("没找到作品节点"); return 1; }

    var keys = sample.Value.EnumerateObject().Select(x => x.Name).ToList();
    Console.WriteLine($"\n作品字段（{keys.Count} 个）：");
    for (int i = 0; i < keys.Count; i += 6)
        Console.WriteLine("    " + string.Join("  ", keys.Skip(i).Take(6)));

    // 重点看标签类字段
    foreach (string k in new[] { "text_extra", "cha_list", "tag", "anchor_info",
                                 "video_tag", "suggest_words", "seo_info" })
    {
        if (!sample.Value.TryGetProperty(k, out var v)) continue;
        string txt = v.ValueKind switch
        {
            JsonValueKind.Null => "null",
            JsonValueKind.Array => $"[{v.GetArrayLength()} 项]",
            JsonValueKind.Object => "(对象)",
            _ => v.ToString(),
        };
        Console.WriteLine($"\n  ★ {k} = {txt}");
        if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
            Console.WriteLine("    首项：" + v[0].ToString()[..Math.Min(300, v[0].ToString().Length)]);
    }
    return 0;
}

// 点赞状态来源调查：dotnet run --project src/DouyinDesktop.Probe -- diggstate
// 回答一个问题：推荐流本身给不给 user_digged？不给的话只能逐条查详情。
if (args.Length > 0 && args[0] == "diggstate")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    var f = await c.GetFeedAsync(10);
    if (!f.Success) { Console.WriteLine("拉推荐失败：" + f.Message); return 1; }

    Console.WriteLine("\n推荐流里的 user_digged：");
    int given = 0;
    foreach (var a in f.Items)
    {
        string v = a.UserDigged is null ? "null（接口没给）" : a.UserDigged.ToString()!;
        if (a.UserDigged is not null) given++;
        Console.WriteLine($"  {a.Id}  {v}");
    }
    Console.WriteLine($"→ {given}/{f.Items.Count} 条带了点赞状态");

    Console.WriteLine("\n逐条查详情对比（前 3 条）：");
    foreach (var a in f.Items.Take(3))
    {
        var d = await c.GetAwemeDetailAsync(a.Id);
        Console.WriteLine($"  {a.Id}  列表={(a.UserDigged?.ToString() ?? "null")}  详情={(d?.UserDigged?.ToString() ?? "null")}");
        await Task.Delay(500);
    }
    return 0;
}

// 个性化对比：两个 webid 各拉一批推荐，看内容差多少
// dotnet run --project src/DouyinDesktop.Probe -- webidcmp
if (args.Length > 0 && args[0] == "webidcmp")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;

    string Read(string name)
    {
        string p = Path.Combine(DouyinPaths.DataDirectory, name);
        return File.Exists(p) ? File.ReadAllText(p).Trim() : "";
    }

    var cases = new (string Label, string WebId)[]
    {
        ("webid-app（程序随机）", Read("webid-app.txt")),
        ("webid.txt（浏览器真实）", Read("webid.txt")),
    };

    var seen = new Dictionary<string, HashSet<string>>();

    foreach (var (label, wid) in cases)
    {
        if (wid.Length < 10) { Console.WriteLine($"{label}：没有值，跳过"); continue; }

        Console.WriteLine($"\n=== {label}  webid={wid} ===");
        var profile = new DeviceProfile { WebId = wid };
        profile.SetBrowserWebId(wid);
        using var c = DouyinClient.Create(profile);
        c.Log += m => Console.WriteLine("    " + m);

        var f = await c.GetFeedAsync(10);
        if (!f.Success) { Console.WriteLine("    失败：" + f.Message); continue; }

        var ids = new HashSet<string>();
        foreach (var a in f.Items)
        {
            ids.Add(a.Id);
            string desc = a.Desc.Length > 30 ? a.Desc[..30] : a.Desc;
            Console.WriteLine($"    @{a.AuthorName,-14} ♥{a.Statistics?.DiggCount,8}  {desc}");
        }
        seen[label] = ids;
        await Task.Delay(1200);
    }

    if (seen.Count == 2)
    {
        var vals = seen.Values.ToList();
        int overlap = vals[0].Intersect(vals[1]).Count();
        Console.WriteLine($"\n两批重合 {overlap} 条 / 各 {vals[0].Count} 条");
    }
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

// 详情接口（IP 属地）：dotnet run --project src/DouyinDesktop.Probe -- iplabel
// 注意：命令名不能再用 detail——上面已经有一个 detail 分支且先 return 了。
if (args.Length > 0 && args[0] == "iplabel")
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

// 我是谁 + 关注了多少人：dotnet run --project src/DouyinDesktop.Probe -- whoami
if (args.Length > 0 && args[0] == "whoami")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    string[] candidates =
    {
        "/aweme/v1/web/im/user/info/",
        "/aweme/v1/web/user/profile/self/",
    };

    foreach (string path in candidates)
    {
        try
        {
            var (status, body) = await c.GetRawAsync(path);
            string head = body.Length == 0 ? "(空)" : body[..Math.Min(400, body.Length)];
            Console.WriteLine($"\n=== {path} ===  HTTP {status}  {body.Length} 字节");
            Console.WriteLine("  " + head.Replace("\n", " "));

            // 顺手把关键字段挑出来
            if (body.Length > 2 && body.TrimStart()[0] == '{')
            {
                using var doc = JsonDocument.Parse(body);
                void Show(string name)
                {
                    if (doc.RootElement.TryGetProperty(name, out var v))
                        Console.WriteLine($"  {name} = {(v.ValueKind == JsonValueKind.Object ? "(对象)" : v.ToString())}");
                }
                foreach (string f in new[] { "user_id", "sec_user_id", "nickname", "unique_id",
                                             "follower_count", "following_count", "aweme_count",
                                             "total_favorited", "mplatform_followers_count" })
                    Show(f);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n=== {path} ===  异常：{ex.Message}");
        }
        await Task.Delay(700);
    }
    return 0;
}

// 登录态 vs 游客态的关注流：内容一样就说明服务端根本没认登录
// dotnet run --project src/DouyinDesktop.Probe -- followguest
if (args.Length > 0 && args[0] == "followguest")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;

    // 第一次：正常登录态
    using (var c = DouyinClient.Create())
    {
        var r = await c.GetFeedScrollAsync(10, 1, "follow/feed");
        var names = r.Success ? r.Items.Select(x => x.AuthorName).ToList() : new();
        Console.WriteLine($"=== 登录态 ===  {(r.Success ? $"{names.Count} 条" : r.Message)}");
        Console.WriteLine("    " + string.Join("、", names.Take(10)));
    }

    await Task.Delay(1500);

    // 第二次：把内存里的登录 Cookie 清掉（不落盘），当游客
    using (var c = DouyinClient.Create())
    {
        var s = await c.EnsureSessionAsync();
        s.LoginCookies = "";
        var r = await c.GetFeedScrollAsync(10, 1, "follow/feed");
        var names = r.Success ? r.Items.Select(x => x.AuthorName).ToList() : new();
        Console.WriteLine($"\n=== 游客态（清空登录 Cookie）===  {(r.Success ? $"{names.Count} 条" : r.Message)}");
        Console.WriteLine("    " + string.Join("、", names.Take(10)));
    }
    return 0;
}

// 两个 webid 的关注流对比：看 follow_status 到底跟着谁走
// dotnet run --project src/DouyinDesktop.Probe -- followcmp
if (args.Length > 0 && args[0] == "followcmp")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;

    string Read(string name)
    {
        string p = Path.Combine(DouyinPaths.DataDirectory, name);
        return File.Exists(p) ? File.ReadAllText(p).Trim() : "";
    }

    var cases = new (string Label, string WebId)[]
    {
        ("webid-app（本机稳定）", Read("webid-app.txt")),
        ("webid.txt（浏览器）", Read("webid.txt")),
    };

    foreach (var (label, wid) in cases)
    {
        if (wid.Length < 10) { Console.WriteLine($"{label}：没有值"); continue; }

        Console.WriteLine($"\n=== {label}  webid={wid} ===");
        var profile = new DeviceProfile { WebId = wid };
        profile.SetBrowserWebId(wid);
        using var c = DouyinClient.Create(profile);

        var r = await c.GetFeedScrollAsync(10, 1, "follow/feed");
        if (!r.Success) { Console.WriteLine("    失败：" + r.Message); await Task.Delay(900); continue; }

        int followed = 0;
        foreach (var a in r.Items)
        {
            bool f = a.Author?.FollowStatus == 1;
            if (f) followed++;
            Console.WriteLine($"    [{(f ? "已关注" : "未关注")}] @{a.AuthorName,-16} {a.Desc[..Math.Min(20, a.Desc.Length)]}");
        }
        Console.WriteLine($"    → 已关注 {followed}/{r.Items.Count}");
        await Task.Delay(1200);
    }
    return 0;
}

// 关注流参数对比：现在的参数是从 tab/feed 抄的，可能让服务端按"推荐"排序
// dotnet run --project src/DouyinDesktop.Probe -- followtest
if (args.Length > 0 && args[0] == "followtest")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    var cases = new (string Name, List<KeyValuePair<string, string>> Extra)[]
    {
        ("A 现在用的（pull_type=2 + min_window）", new()
        {
            new("count", "10"), new("refresh_index", "1"),
            new("video_type_select", "1"), new("pull_type", "2"),
            new("min_window", "0"), new("view_count", "2"),
        }),
        ("B pull_type=0", new()
        {
            new("count", "10"), new("refresh_index", "1"),
            new("video_type_select", "1"), new("pull_type", "0"),
            new("min_window", "0"), new("view_count", "2"),
        }),
        ("C 精简（只留 count/refresh_index）", new()
        {
            new("count", "10"), new("refresh_index", "1"),
        }),
    };

    foreach (var (name, extra) in cases)
    {
        try
        {
            var (status, body) = await c.GetRawAsync("/aweme/v1/web/follow/feed/", extra);
            var rows = ParseFollowBody(body);

            Console.WriteLine($"\n=== {name} ===  HTTP {status}  {body.Length} 字节  {rows.Count} 条");
            foreach (var (nick, fs, t) in rows.Take(10))
            {
                string when = t > 0 ? DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().ToString("MM-dd HH:mm") : "?";
                Console.WriteLine($"    [{(fs == 1 ? "已关注" : "未关注")}] @{nick,-16} {when}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n=== {name} ===  异常：{ex.Message}");
        }
        await Task.Delay(1000);
    }
    return 0;
}

// 关注流 refresh_index 实验：递增会不会拉到更旧的内容
// dotnet run --project src/DouyinDesktop.Probe -- followri
if (args.Length > 0 && args[0] == "followri")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    foreach (int ri in new[] { 0, 1, 2, 5, 12 })
    {
        var r = await c.GetFeedScrollAsync(10, ri, "follow/feed");
        if (!r.Success) { Console.WriteLine($"refresh_index={ri,-3} 失败：{r.Message}"); continue; }

        var times = r.Items.Select(x => x.CreateTime).Where(t => t > 0).OrderByDescending(t => t).ToList();
        string newest = times.Count > 0
            ? DateTimeOffset.FromUnixTimeSeconds(times[0]).ToLocalTime().ToString("MM-dd HH:mm")
            : "?";
        string oldest = times.Count > 0
            ? DateTimeOffset.FromUnixTimeSeconds(times[^1]).ToLocalTime().ToString("MM-dd HH:mm")
            : "?";
        int followed = r.Items.Count(x => x.Author?.FollowStatus == 1);

        Console.WriteLine($"refresh_index={ri,-3} {r.Items.Count,2} 条  已关注 {followed,2}  " +
                          $"最新 {newest}  最旧 {oldest}  {r.ElapsedMs}ms");
        await Task.Delay(900);
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
        Console.WriteLine($"\n=== {ep} ===  " + (r.Success ? $"{r.Items.Count} 条" : r.Message));
        if (!r.Success) continue;

        int followed = 0;
        foreach (var a in r.Items)
        {
            string fs = a.Author?.FollowStatus == 1 ? "已关注" : "未关注";
            if (a.Author?.FollowStatus == 1) followed++;
            Console.WriteLine($"    [{fs}] @{a.AuthorName,-16} {a.Desc[..Math.Min(24, a.Desc.Length)]}");
        }
        Console.WriteLine($"    → 已关注作者 {followed}/{r.Items.Count} 条");
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

static List<(string Nick, int Follow, long Time)> ParseFollowBody(string body)
{
    var list = new List<(string, int, long)>();
    if (string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] != '{') return list;

    try
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var node in arr.EnumerateArray())
        {
            var aw = node.TryGetProperty("aweme", out var a) && a.ValueKind == JsonValueKind.Object ? a : node;

            string nick = "";
            int fs = -1;
            if (aw.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object)
            {
                if (au.TryGetProperty("nickname", out var n) && n.ValueKind == JsonValueKind.String)
                    nick = n.GetString() ?? "";
                if (au.TryGetProperty("follow_status", out var f) && f.ValueKind == JsonValueKind.Number)
                    fs = f.GetInt32();
            }

            long t = aw.TryGetProperty("create_time", out var ct) && ct.ValueKind == JsonValueKind.Number
                ? ct.GetInt64() : 0;

            list.Add((nick, fs, t));
        }
    }
    catch (JsonException) { }
    return list;
}

static string Short(string s, int max)
    => string.IsNullOrEmpty(s) ? "(无文案)" : s.Length <= max ? s : s[..max] + "…";

/// <summary>把 data/api-templates.json 挂到客户端上，和主程序的行为保持一致。</summary>
static void AttachTemplates(DouyinClient client)
{
    try
    {
        string path = Path.Combine(DouyinPaths.DataDirectory, "api-templates.json");
        if (!File.Exists(path))
        {
            Console.WriteLine("TPL=none  DIR=" + DouyinPaths.DataDirectory);
            return;
        }

        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        if (map is null || map.Count == 0)
        {
            Console.WriteLine("TPL=empty");
            return;
        }

        client.GetTemplate = p => map.TryGetValue(p, out var u) ? u : null;
        Console.WriteLine("TPL=" + string.Join(",", map.Keys));
    }
    catch (Exception ex)
    {
        Console.WriteLine("TPL 读取失败：" + ex.Message);
    }
}
