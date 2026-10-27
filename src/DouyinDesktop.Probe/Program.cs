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

// presented_ids 累积会不会把推荐拖向"大众"
// dotnet run --project src/DouyinDesktop.Probe -- feedpres
if (args.Length > 0 && args[0] == "feedpres")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    Console.WriteLine("=== A. 不传 presented_ids（每次全新）===");
    for (int i = 1; i <= 4; i++)
    {
        var r = await c.GetFeedAsync(10);
        if (r.Success)
            Console.WriteLine($"  {i}: {string.Join("、", r.Items.Take(6).Select(x => x.AuthorName))}");
        await Task.Delay(1400);
    }

    Console.WriteLine("\n=== B. 累积 presented_ids（模拟主程序翻页）===");
    var seen = new HashSet<string>(StringComparer.Ordinal);
    for (int i = 1; i <= 4; i++)
    {
        var r = await c.GetFeedAsync(10, seen);
        if (!r.Success) { Console.WriteLine($"  {i}: 失败 {r.Message}"); continue; }

        foreach (var a in r.Items) seen.Add(a.Id);
        Console.WriteLine($"  {i}: [{seen.Count} 个已展示] {string.Join("、", r.Items.Take(6).Select(x => x.AuthorName))}");
        await Task.Delay(1400);
    }
    return 0;
}

// 精确复刻浏览器搜索请求：dotnet run --project src/DouyinDesktop.Probe -- search2 美女
//
// 参数表来自用户从 Edge F12 复制的真实 cURL。关键三点：
//   1. 用的是 **a_bogus**（不是 x_bogus，知乎那篇文章误导了）
//   2. webid 是浏览器的 7548817309171861034，和我们 webid.txt 里的不一样
//   3. 比我们之前多 5 个参数：enable_history / disable_rs / need_filter_settings /
//      list_type / pc_search_top_1_params
if (args.Length > 0 && args[0] == "search2")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    string kw = args.Length > 1 ? args[1] : "美女";
    int off = args.Length > 2 && int.TryParse(args[2], out int o) ? o : 0;

    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    var (status, body) = await c.SearchRawAsync2(kw, off);

    Console.WriteLine($"\n=== 精确复刻 ===  HTTP {status}  {body.Length} 字节");
    Console.WriteLine("  " + body[..Math.Min(300, body.Length)].Replace("\n", " "));

    if (body.Length > 2 && body.TrimStart()[0] == '{')
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("status_code", out var sc))
            Console.WriteLine($"  status_code = {sc}");

        Console.WriteLine("  顶层字段：" + string.Join(", ",
            doc.RootElement.EnumerateObject().Select(p => p.Name)));
        foreach (string k in new[] { "search_id", "cursor", "has_more", "log_pb", "extra" })
            if (doc.RootElement.TryGetProperty(k, out var v))
                Console.WriteLine($"    {k} = {(v.ValueKind == JsonValueKind.Object
                    ? Short(v.ToString(), 160) : v.ToString())}");

        if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            int n = 0;
            foreach (var node in arr.EnumerateArray())
            {
                if (!node.TryGetProperty("aweme_info", out var ai)) continue;
                n++;
                if (n <= 8)
                {
                    string nick = ai.TryGetProperty("author", out var au) && au.TryGetProperty("nickname", out var nn)
                        ? nn.GetString() ?? "" : "";
                    Console.WriteLine($"    @{nick}");
                }
            }
            Console.WriteLine($"  → {n} 条作品");
        }
    }

    // 走正式入口（解析成 Aweme），确认 data[].aweme_info 这条链路通
    var parsed = await c.SearchAsync(kw, off);
    Console.WriteLine($"\n=== SearchAsync === success={parsed.Success}  " +
                      $"{parsed.Items.Count} 条  hasMore={parsed.HasMore}  next={parsed.NextOffset}  {parsed.Message}");
    foreach (var it in parsed.Items.Take(3))
        Console.WriteLine($"    @{it.AuthorName}  {Short(it.Desc, 28)}  " +
                          $"时长 {(it.Video?.Duration ?? 0) / 1000}s");
    return 0;
}

// 两个推荐接口各自连续拉，看谁会退化成"同一批大众视频"
// dotnet run --project src/DouyinDesktop.Probe -- feedmix
if (args.Length > 0 && args[0] == "feedmix")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    var sets = new Dictionary<string, List<HashSet<string>>>();

    async Task Run(string label, bool scroll)
    {
        Console.WriteLine($"\n===== {label} =====");
        var rounds = new List<HashSet<string>>();

        for (int i = 1; i <= 5; i++)
        {
            var r = scroll
                ? await c.GetFeedScrollAsync(10, i)
                : await c.GetFeedAsync(10);

            if (!r.Success) { Console.WriteLine($"  第{i}轮 失败：{r.Message}"); continue; }

            var ids = r.Items.Select(x => x.Id).ToHashSet();
            rounds.Add(ids);

            string names = string.Join("、", r.Items.Take(6).Select(x => x.AuthorName));
            Console.WriteLine($"  第{i}轮 {r.Items.Count,2} 条：{names}");
            await Task.Delay(1500);
        }

        // 相邻两轮的重合度 —— 越接近 100% 越说明"每次给同一批"
        for (int i = 1; i < rounds.Count; i++)
        {
            int overlap = rounds[i].Intersect(rounds[i - 1]).Count();
            Console.WriteLine($"  第{i}→{i + 1}轮 重合 {overlap}/{rounds[i].Count}");
        }

        sets[label] = rounds;
    }

    await Run("module/feed（GetFeedAsync）", scroll: false);
    await Run("tab/feed（GetFeedScrollAsync）", scroll: true);

    // 两个接口之间的重合度
    if (sets.Count == 2)
    {
        var a = sets["module/feed（GetFeedAsync）"];
        var b = sets["tab/feed（GetFeedScrollAsync）"];
        if (a.Count > 0 && b.Count > 0)
        {
            int overlap = a[^1].Intersect(b[^1]).Count();
            Console.WriteLine($"\n最后两轮跨接口重合：{overlap} 条");
        }
    }
    return 0;
}

// 搜索接口：dotnet run --project src/DouyinDesktop.Probe -- search 美女
//
// 参数表来自 https://zhuanlan.zhihu.com/p/703258418
// 关键：这个接口用的是 **x_bogus**（不是 a_bogus），项目里目前只有 a_bogus。
// 先补齐完整参数 + a_bogus 试，看 2483 是"参数不全"还是"签名不对"。
if (args.Length > 0 && args[0] == "search")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    string kw = args.Length > 1 ? args[1] : "美女";

    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    var extra = new List<KeyValuePair<string, string>>
    {
        new("search_channel", "aweme_video_web"),
        new("sort_type", "0"),
        new("publish_time", "0"),
        new("keyword", kw),
        new("search_source", "switch_tab"),
        new("query_correct_type", "1"),
        new("is_filter_search", "0"),
        new("from_group_id", ""),
        new("offset", "0"),
        new("count", "10"),
    };

    try
    {
        var (status, body) = await c.SearchRawAsync(kw);

        Console.WriteLine($"\n=== x_bogus 签名 ===  HTTP {status}  {body.Length} 字节");
        Console.WriteLine("  " + body[..Math.Min(260, body.Length)].Replace("\n", " "));

        if (body.Length > 2 && body.TrimStart()[0] == '{')
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("status_code", out var sc))
                Console.WriteLine($"  status_code = {sc}");

            if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                int n = 0;
                foreach (var node in arr.EnumerateArray())
                {
                    if (!node.TryGetProperty("aweme_info", out var ai)) continue;
                    n++;
                    if (n <= 6)
                    {
                        string nick = ai.TryGetProperty("author", out var au) && au.TryGetProperty("nickname", out var nn)
                            ? nn.GetString() ?? "" : "";
                        Console.WriteLine($"    @{nick}");
                    }
                }
                Console.WriteLine($"  → {n} 条作品");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("异常：" + ex.Message);
    }
    return 0;
}

// 一次能拉多少条：dotnet run --project src/DouyinDesktop.Probe -- bigfeed
// 标签过滤后经常剩不下几条，想一次多拉点，先摸清 count 的上限。
if (args.Length > 0 && args[0] == "bigfeed")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    foreach (int n in new[] { 20, 30, 50, 80 })
    {
        string body = await c.GetFeedRawAsync(n);
        if (body.Length == 0) { Console.WriteLine($"count={n,-3} 空 body"); await Task.Delay(1200); continue; }

        int got = 0;
        using (var doc = JsonDocument.Parse(body))
        {
            if (doc.RootElement.TryGetProperty("aweme_list", out var al) && al.ValueKind == JsonValueKind.Array)
                got = al.GetArrayLength();
            else if (doc.RootElement.TryGetProperty("cards", out var cards) && cards.ValueKind == JsonValueKind.Array)
            {
                foreach (var card in cards.EnumerateArray())
                    if (card.TryGetProperty("aweme", out _)) got++;
            }
        }

        Console.WriteLine($"count={n,-3} 返回 {got,2} 条  body {body.Length / 1024} KB");
        await Task.Delay(1500);
    }
    return 0;
}

// 详情接口有没有 video_tag：dotnet run --project src/DouyinDesktop.Probe -- dtags
if (args.Length > 0 && args[0] == "dtags")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    var f = await c.GetFeedAsync(6);
    if (!f.Success) { Console.WriteLine("拉推荐失败：" + f.Message); return 1; }

    foreach (var item in f.Items.Take(4))
    {
        var detail = await c.GetAwemeDetailAsync(item.Id);

        string textTags = detail is null || detail.Hashtags.Count == 0 ? "-" : string.Join(" ", detail.Hashtags);
        string videoTags = detail is null || detail.OfficialTags.Count == 0 ? "-" : string.Join(" ", detail.OfficialTags);

        Console.WriteLine($"  {item.Id}  {(detail is null ? "详情失败" : "ok")}");
        Console.WriteLine($"      作者标签[{textTags}]");
        Console.WriteLine($"      官方标签[{videoTags}]");
        await Task.Delay(800);
    }
    return 0;
}

// 官方标签 video_tag 覆盖率：dotnet run --project src/DouyinDesktop.Probe -- vtags
// text_extra 是作者自己加的；video_tag 是平台给的，作者没加标签时也能有。
if (args.Length > 0 && args[0] == "vtags")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    int total = 0, hasText = 0, hasVideo = 0, hasEither = 0;

    for (int page = 0; page < 3; page++)
    {
        string body = await c.GetFeedRawAsync(10);
        if (body.Length == 0) { Console.WriteLine("空 body"); break; }

        var nodes = new List<JsonElement>();
        using (var doc = JsonDocument.Parse(body))
        {
            if (doc.RootElement.TryGetProperty("aweme_list", out var al) && al.ValueKind == JsonValueKind.Array)
                foreach (var a in al.EnumerateArray()) nodes.Add(a.Clone());
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

            var textTags = new List<string>();
            if (aw.TryGetProperty("text_extra", out var te) && te.ValueKind == JsonValueKind.Array)
                foreach (var t in te.EnumerateArray())
                    if (t.TryGetProperty("hashtag_name", out var h) && h.ValueKind == JsonValueKind.String)
                    {
                        string v = h.GetString() ?? "";
                        if (v.Length > 0) textTags.Add(v);
                    }

            var vTags = new List<string>();
            if (aw.TryGetProperty("video_tag", out var vt) && vt.ValueKind == JsonValueKind.Array)
                foreach (var t in vt.EnumerateArray())
                    if (t.TryGetProperty("tag_name", out var n) && n.ValueKind == JsonValueKind.String)
                    {
                        string v = n.GetString() ?? "";
                        if (v.Length > 0) vTags.Add(v);
                    }

            if (textTags.Count > 0) hasText++;
            if (vTags.Count > 0) hasVideo++;
            if (textTags.Count > 0 || vTags.Count > 0) hasEither++;

            string nick = "";
            if (aw.TryGetProperty("author", out var au) && au.TryGetProperty("nickname", out var nn) && nn.ValueKind == JsonValueKind.String)
                nick = nn.GetString() ?? "";

            Console.WriteLine($"  @{nick,-13} 作者标签[{(textTags.Count == 0 ? "-" : string.Join(" ", textTags))}]" +
                              $"  官方标签[{(vTags.Count == 0 ? "-" : string.Join(" ", vTags))}]");
        }
        await Task.Delay(1200);
    }

    Console.WriteLine($"\n共 {total} 条：作者标签 {hasText} 条（{hasText * 100 / Math.Max(1, total)}%），" +
                      $"官方标签 {hasVideo} 条（{hasVideo * 100 / Math.Max(1, total)}%），" +
                      $"至少一个 {hasEither} 条（{hasEither * 100 / Math.Max(1, total)}%）");
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

// 喜欢 / 收藏 到底能不能拉：dotnet run --project src/DouyinDesktop.Probe -- fav
// 一个个候选路径试过去，把 HTTP 状态、body 长度、status_code 都打出来。
// 目的只有一个：确认"读方向"通不通，别猜。
if (args.Length > 0 && args[0] == "fav")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    var s0 = await c.EnsureSessionAsync();
    Console.WriteLine($"登录态：{(string.IsNullOrEmpty(s0.LoginCookies) ? "游客（无 Cookie）" : "已登录")}");
    string widPath = Path.Combine(DouyinPaths.DataDirectory, "webid.txt");
    Console.WriteLine($"webid  = {(File.Exists(widPath) ? File.ReadAllText(widPath).Trim() : "(无 webid.txt)")}");
    Console.WriteLine($"uifid  = {(string.IsNullOrEmpty(s0.EffectiveUifid) ? "(空)" : s0.EffectiveUifid)}");

    // 候选路径 + 各自最小参数。路径写法来自网页端习惯，错了就是 404/空 body，不会炸。
    var cases = new (string Label, string Path, bool WebSign, KeyValuePair<string, string>[] Extra)[]
    {
        ("喜欢 favorite",
            "/aweme/v1/web/aweme/favorite/", false,
            new[] { new KeyValuePair<string, string>("count", "10"),
                    new KeyValuePair<string, string>("max_cursor", "0") }),

        ("喜欢 favorite +webSign",
            "/aweme/v1/web/aweme/favorite/", true,
            new[] { new KeyValuePair<string, string>("count", "10"),
                    new KeyValuePair<string, string>("max_cursor", "0") }),

        ("收藏 listcollection",
            "/aweme/v1/web/aweme/listcollection/", false,
            new[] { new KeyValuePair<string, string>("count", "10"),
                    new KeyValuePair<string, string>("cursor", "0") }),

        ("收藏 listcollection +webSign",
            "/aweme/v1/web/aweme/listcollection/", true,
            new[] { new KeyValuePair<string, string>("count", "10"),
                    new KeyValuePair<string, string>("cursor", "0") }),

        ("收藏夹分类 collect/list",
            "/aweme/v1/web/aweme/collect/list/", false,
            new[] { new KeyValuePair<string, string>("count", "10"),
                    new KeyValuePair<string, string>("cursor", "0") }),

        ("收藏夹分类 +webSign",
            "/aweme/v1/web/aweme/collect/list/", true,
            new[] { new KeyValuePair<string, string>("count", "10"),
                    new KeyValuePair<string, string>("cursor", "0") }),

        ("我的信息 profile/self",
            "/aweme/v1/web/user/profile/self/", true,
            Array.Empty<KeyValuePair<string, string>>()),
    };

    foreach (var (label, path, webSign, extra) in cases)
    {
        try
        {
            var (status, body) = await c.GetRawAsync(path, extra, webSign);

            string code = "-";
            int n = 0;
            if (body.Length > 2 && body.TrimStart()[0] == '{')
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("status_code", out var sc) && sc.ValueKind == JsonValueKind.Number)
                        code = sc.GetInt32().ToString();
                    // 顺带数一下拿到几条作品
                    if (doc.RootElement.TryGetProperty("aweme_list", out var al) && al.ValueKind == JsonValueKind.Array)
                        n = al.GetArrayLength();
                }
                catch (JsonException) { }
            }

            Console.WriteLine($"\n=== {label} ===");
            Console.WriteLine($"  HTTP {status}  {body.Length} 字节  status_code={code}  作品 {n} 条");
            if (body.Length > 0)
                Console.WriteLine("  " + body[..Math.Min(220, body.Length)].Replace("\n", " "));

            // 有作品就抽一条出来看看字段（标题 + 标签）
            if (n > 0)
            {
                using var doc = JsonDocument.Parse(body);
                var head = doc.RootElement.GetProperty("aweme_list")[0];
                string desc = head.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String
                    ? (d.GetString() ?? "") : "";
                string nick = "";
                if (head.TryGetProperty("author", out var au) &&
                    au.TryGetProperty("nickname", out var nk) && nk.ValueKind == JsonValueKind.String)
                    nick = nk.GetString() ?? "";
                Console.WriteLine($"  首条：@{nick}  {Short(desc, 40)}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n=== {label} ===\n  异常：{ex.Message}");
        }

        await Task.Delay(900);
    }
    return 0;
}

// 喜欢接口的参数矩阵 + 登录态对照：dotnet run --project src/DouyinDesktop.Probe -- fav2
// fav 已经确认：`aweme/favorite/` 存在，不签 webSign 是 403 Argus，签了是 200 但 aweme_list=null。
// 这一轮只回答两个问题：① 缺哪个参数 ② 当前登录态到底健康不健康
if (args.Length > 0 && args[0] == "fav2")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    var s = await c.EnsureSessionAsync();
    bool logged = !string.IsNullOrEmpty(s.LoginCookies);
    Console.WriteLine($"登录态：{(logged ? "已登录" : "游客")}  msToken={(s.MsToken.Length > 0 ? "有" : "无")}");

    // ---- 对照 1：作者主页（已知可用，能返回 20 条左右）----
    Console.WriteLine("\n=== 对照：aweme/post（已知可用）===");
    var fb = await c.GetFeedAsync(5);
    if (fb.Success && fb.Items.Count > 0)
    {
        string uid = fb.Items[0].Author.SecUid;
        Console.WriteLine($"  推荐流拿到 {fb.Items.Count} 条，取作者 sec_uid={Short(uid, 40)}");
        var posts = await c.GetUserPostsAsync(uid);
        Console.WriteLine($"  作者主页：{(posts.Success ? posts.Items.Count + " 条" : "失败 " + posts.Message)}");
    }
    else Console.WriteLine("  推荐流失败：" + fb.Message);

    await Task.Delay(1200);

    // ---- 对照 2：评论（强校验接口，能反映登录态）----
    Console.WriteLine("\n=== 对照：评论 ===");
    var withComment = fb.Success ? fb.Items.FirstOrDefault(x => (x.Statistics?.CommentCount ?? 0) > 0) : null;
    if (withComment is not null)
    {
        var page = await c.GetCommentsAsync(withComment.Id, 0, 20);
        Console.WriteLine($"  作品 {withComment.Id} 评论 {page.Comments.Count} 条 {(page.Comments.Count == 0 ? "← 空，登录态可疑" : "")}");
    }
    else Console.WriteLine("  这批里没有带评论的作品，跳过");

    await Task.Delay(1200);

    // ---- 喜欢接口：逐个补参数，看哪个能让 aweme_list 不是 null ----
    Console.WriteLine("\n=== 喜欢 favorite：参数矩阵（全部走 webSign）===");

    var matrix = new (string Label, KeyValuePair<string, string>[] Extra)[]
    {
        ("只有 count+cursor", new[]
        {
            new KeyValuePair<string, string>("count", "10"),
            new KeyValuePair<string, string>("max_cursor", "0"),
        }),
        ("加 min_cursor", new[]
        {
            new KeyValuePair<string, string>("count", "10"),
            new KeyValuePair<string, string>("max_cursor", "0"),
            new KeyValuePair<string, string>("min_cursor", "0"),
        }),
        ("加空 sec_user_id", new[]
        {
            new KeyValuePair<string, string>("sec_user_id", ""),
            new KeyValuePair<string, string>("count", "10"),
            new KeyValuePair<string, string>("max_cursor", "0"),
        }),
        ("加 publish_video_strategy_type", new[]
        {
            new KeyValuePair<string, string>("count", "10"),
            new KeyValuePair<string, string>("max_cursor", "0"),
            new KeyValuePair<string, string>("publish_video_strategy_type", "2"),
        }),
        ("浏览器全套（含 from_user_page/support_dash）", new[]
        {
            new KeyValuePair<string, string>("sec_user_id", ""),
            new KeyValuePair<string, string>("max_cursor", "0"),
            new KeyValuePair<string, string>("min_cursor", "0"),
            new KeyValuePair<string, string>("count", "10"),
            new KeyValuePair<string, string>("publish_video_strategy_type", "2"),
            new KeyValuePair<string, string>("from_user_page", "1"),
            new KeyValuePair<string, string>("support_dash", "1"),
        }),
    };

    foreach (var (label, extra) in matrix)
    {
        try
        {
            var (status, body) = await c.GetRawAsync("/aweme/v1/web/aweme/favorite/", extra, useWebSign: true);
            Console.WriteLine($"  {label,-40} HTTP {status}  {body.Length} 字节  {Short(body, 90)}");
        }
        catch (Exception ex) { Console.WriteLine($"  {label,-40} 异常 {ex.Message}"); }
        await Task.Delay(900);
    }

    // ---- 收藏：路径变体（listcollection/collect 都是 404，换个写法试）----
    Console.WriteLine("\n=== 收藏：路径变体（走 webSign）===");
    string[] favPaths =
    {
        "/aweme/v1/web/aweme/collection/list/",
        "/aweme/v1/web/collect/list/",
        "/aweme/v1/web/aweme/list/collection/",
        "/aweme/v1/web/user/collect/list/",
        "/aweme/v1/web/aweme/collect/",
    };
    foreach (string path in favPaths)
    {
        try
        {
            var (status, body) = await c.GetRawAsync(path, new[]
            {
                new KeyValuePair<string, string>("count", "10"),
                new KeyValuePair<string, string>("cursor", "0"),
            }, useWebSign: true);
            Console.WriteLine($"  {path,-40} HTTP {status}  {body.Length} 字节  {Short(body, 70)}");
        }
        catch (Exception ex) { Console.WriteLine($"  {path,-40} 异常 {ex.Message}"); }
        await Task.Delay(800);
    }

    return 0;
}

// favorite 只差 sec_user_id？拿别人的 sec_uid 试一下参数对不对
// dotnet run --project src/DouyinDesktop.Probe -- fav3
if (args.Length > 0 && args[0] == "fav3")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    var fb = await c.GetFeedAsync(5);
    if (!fb.Success || fb.Items.Count == 0) { Console.WriteLine("推荐流失败"); return 1; }

    // 取前 3 个作者的 sec_uid 逐个试
    var uids = fb.Items.Select(x => x.Author.SecUid).Where(x => x.Length > 10).Distinct().Take(3).ToList();
    Console.WriteLine($"候选 sec_uid {uids.Count} 个\n");

    foreach (string uid in uids)
    {
        try
        {
            var (status, body) = await c.GetRawAsync("/aweme/v1/web/aweme/favorite/", new[]
            {
                new KeyValuePair<string, string>("sec_user_id", uid),
                new KeyValuePair<string, string>("count", "10"),
                new KeyValuePair<string, string>("max_cursor", "0"),
                new KeyValuePair<string, string>("min_cursor", "0"),
                new KeyValuePair<string, string>("publish_video_strategy_type", "2"),
            }, useWebSign: true);

            int n = 0;
            if (body.Length > 2 && body.TrimStart()[0] == '{')
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("aweme_list", out var al) && al.ValueKind == JsonValueKind.Array)
                        n = al.GetArrayLength();
                }
                catch (JsonException) { }
            }

            Console.WriteLine($"sec_uid={Short(uid, 30)}  HTTP {status}  {body.Length}B  作品 {n} 条  {Short(body, 80)}");
        }
        catch (Exception ex) { Console.WriteLine($"sec_uid={Short(uid, 30)}  异常 {ex.Message}"); }
        await Task.Delay(1000);
    }

    // 顺手试：profile/other 能不能用数字 uid 换 sec_uid
    Console.WriteLine("\n=== profile/other 试探 ===");
    string[] tryIds = { "7572717252269574195" };
    foreach (string id in tryIds)
    {
        var (st, bd) = await c.GetRawAsync("/aweme/v1/web/user/profile/other/", new[]
        {
            new KeyValuePair<string, string>("sec_user_id", ""),
            new KeyValuePair<string, string>("user_id", id),
            new KeyValuePair<string, string>("publish_video_strategy_type", "2"),
        }, useWebSign: true);
        Console.WriteLine($"  user_id={id}  HTTP {st}  {bd.Length}B  {Short(bd, 160)}");
        await Task.Delay(800);
    }

    return 0;
}

// 浏览器打开自己主页时发出的"当前用户"类接口，纯 HTTP 能不能通
// dotnet run --project src/DouyinDesktop.Probe -- quser
if (args.Length > 0 && args[0] == "quser")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    string[] paths =
    {
        "/aweme/v1/web/query/user/",
        "/aweme/v1/web/social/count",
        "/aweme/v1/web/get/user/settings/",
        "/aweme/v1/web/get/user/risklevel/",
    };

    foreach (string path in paths)
    {
        foreach (bool ws in new[] { false, true })
        {
            try
            {
                var (status, body) = await c.GetRawAsync(path, null, useWebSign: ws);
                Console.WriteLine($"\n=== {path}  webSign={ws} ===");
                Console.WriteLine($"  HTTP {status}  {body.Length} 字节");
                if (body.Length > 0) Console.WriteLine("  " + Short(body, 400));

                // 一旦看到 sec_uid 就单独标出来——这是这次真正要的东西
                int i = body.IndexOf("MS4wLjABAAAA", StringComparison.Ordinal);
                if (i >= 0)
                {
                    int end = i;
                    while (end < body.Length && "\"',& ".IndexOf(body[end]) < 0) end++;
                    Console.WriteLine($"  ★ sec_uid = {body[i..end]}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"\n=== {path}  webSign={ws} ===\n  异常 {ex.Message}"); }
            await Task.Delay(900);
        }
    }
    return 0;
}

// 有了数字 uid 之后，怎么换成 sec_uid / 直接拉喜欢
// dotnet run --project src/DouyinDesktop.Probe -- fav4
if (args.Length > 0 && args[0] == "fav4")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    // 1) 先问 query/user 要自己的数字 uid
    var (qs, qb) = await c.GetRawAsync("/aweme/v1/web/query/user/");
    string uid = "";
    if (qb.Length > 2)
    {
        try
        {
            using var doc = JsonDocument.Parse(qb);
            if (doc.RootElement.TryGetProperty("user_uid", out var v) && v.ValueKind == JsonValueKind.String)
                uid = v.GetString() ?? "";
        }
        catch (JsonException) { }
    }
    Console.WriteLine($"query/user → HTTP {qs}  数字 uid = {(uid.Length > 0 ? uid : "(没拿到)")}");
    if (uid.Length == 0) return 1;

    await Task.Delay(800);

    // 2) profile/other 用数字 uid，看能不能换出 sec_uid
    Console.WriteLine("\n=== profile/other 用 user_id ===");
    foreach (bool ws in new[] { false, true })
    {
        var (st, bd) = await c.GetRawAsync("/aweme/v1/web/user/profile/other/", new[]
        {
            new KeyValuePair<string, string>("user_id", uid),
            new KeyValuePair<string, string>("publish_video_strategy_type", "2"),
        }, useWebSign: ws);
        Console.WriteLine($"  webSign={ws}  HTTP {st}  {bd.Length}B  {Short(bd, 200)}");
        int i = bd.IndexOf("MS4wLjABAAAA", StringComparison.Ordinal);
        if (i >= 0)
        {
            int end = i;
            while (end < bd.Length && "\"',& ".IndexOf(bd[end]) < 0) end++;
            Console.WriteLine($"  ★ sec_uid = {bd[i..end]}");
        }
        await Task.Delay(900);
    }

    // 3) 喜欢接口直接喂 user_id
    Console.WriteLine("\n=== favorite 用 user_id ===");
    foreach (string key in new[] { "user_id", "sec_user_id" })
    {
        var (st, bd) = await c.GetRawAsync("/aweme/v1/web/aweme/favorite/", new[]
        {
            new KeyValuePair<string, string>(key, uid),
            new KeyValuePair<string, string>("count", "10"),
            new KeyValuePair<string, string>("max_cursor", "0"),
            new KeyValuePair<string, string>("min_cursor", "0"),
        }, useWebSign: true);
        int n = 0;
        try
        {
            using var doc = JsonDocument.Parse(bd);
            if (doc.RootElement.TryGetProperty("aweme_list", out var al) && al.ValueKind == JsonValueKind.Array)
                n = al.GetArrayLength();
        }
        catch (JsonException) { }
        Console.WriteLine($"  {key}={uid}  HTTP {st}  {bd.Length}B  作品 {n} 条  {Short(bd, 120)}");
        await Task.Delay(1000);
    }

    return 0;
}

// 推荐倾向分析（设置页那个功能的命令行版）：dotnet run --project src/DouyinDesktop.Probe -- taste
// 拉 5 批推荐，统计话题标签，看主题分布对不对。
if (args.Length > 0 && args[0] == "taste")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    var collected = new List<DouyinDesktop.Core.Models.Aweme>();
    var ids = new List<string>();

    for (int i = 0; i < 2; i++)
    {
        Console.Write($"第 {i + 1}/2 批… ");
        var r = await c.GetFeedAsync(20, ids);
        if (!r.Success) { Console.WriteLine("失败：" + r.Message); }
        else
        {
            collected.AddRange(r.Items);
            ids.AddRange(r.Items.Select(x => x.Id));
            Console.WriteLine($"{r.Items.Count} 条（累计 {collected.Count}）");
        }
        if (i < 1) await Task.Delay(1200);
    }

    var distinct = collected.GroupBy(x => x.Id).Select(g => g.First()).ToList();

    // 逐条取详情补平台标签（推荐流里 video_tag 是 0%）
    Console.WriteLine($"\n逐条取详情补平台标签（{distinct.Count} 条，约 {distinct.Count * 0.6:0} 秒）…");
    var swDetail = System.Diagnostics.Stopwatch.StartNew();
    int officialOk = 0;
    for (int i = 0; i < distinct.Count; i++)
    {
        var d = await c.GetAwemeDetailAsync(distinct[i].Id);
        if (d is not null)
        {
            if (d.OfficialTags.Count > 0) { officialOk++; distinct[i] = d; }
        }
        if ((i + 1) % 10 == 0) Console.Write($" {i + 1}");
        await Task.Delay(300);
    }
    swDetail.Stop();
    Console.WriteLine($"\n详情完成：{swDetail.Elapsed.TotalSeconds:0.0}s，平台标签 {officialOk}/{distinct.Count}\n");

    var rep = TasteAnalyzer.Analyze(distinct);

    Console.WriteLine($"=== 采样 {rep.Sampled} 条，带标签 {rep.WithTags} 条（{rep.TagCoverage:P0}），" +
                      $"平均每条 {(rep.Sampled == 0 ? 0 : (double)rep.TagTotal / rep.Sampled):0.0} 个标签，" +
                      $"已归类 {rep.Classified} 条，平台标签 {rep.OfficialCovered} 条 ===");
    Console.WriteLine($"结论：{rep.Summary}\n");

    Console.WriteLine("主题分布：");
    foreach (var t in rep.Themes)
        Console.WriteLine($"  {t.Theme,-10} {t.Count,3} 条  {t.Percent,6:P0}   {(t.Samples.Count > 0 ? "例：" + string.Join("、", t.Samples) : "")}");

    Console.WriteLine("\n高频标签：");
    for (int i = 0; i < rep.TopTags.Count; i += 5)
        Console.WriteLine("  " + string.Join("  ", rep.TopTags.Skip(i).Take(5).Select(x => $"#{x.Tag}({x.Count})")));

    Console.WriteLine("\n作者集中度：");
    foreach (var a in rep.TopAuthors)
        Console.WriteLine($"  {a.Name,-16} {a.Count,2} 条（{(double)a.Count / Math.Max(1, rep.Sampled):P0}）");

    return 0;
}

// 登录态到底算不算"有效"：dotnet run --project src/DouyinDesktop.Probe -- loginstat
// 走 VerifyLoginAsync（profile/self 主探针 + 点赞兜底），验证判断逻辑本身。
if (args.Length > 0 && args[0] == "loginstat")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    var s = await c.EnsureSessionAsync();
    Console.WriteLine($"IsLoggedIn（只看有没有 Cookie）= {s.IsLoggedIn}");

    bool? ok = await c.VerifyLoginAsync();
    Console.WriteLine($"VerifyLoginAsync = {(ok is null ? "判断不了（网络问题）" : ok.Value ? "有效" : "无效/未登录")}");

    // 把两个探针的原始返回都打出来，方便对照
    var (_, self) = await c.GetRawAsync("/aweme/v1/web/user/profile/self/", null, useWebSign: true);
    Console.WriteLine($"\nprofile/self：{Short(self, 160)}");

    var (_, qu) = await c.GetRawAsync("/aweme/v1/web/query/user/");
    Console.WriteLine($"query/user  ：{Short(qu, 160)}");
    return 0;
}

// 登录后 dump profile/self 的完整响应，找自己的 sec_uid
// dotnet run --project src/DouyinDesktop.Probe -- selfdump
if (args.Length > 0 && args[0] == "selfdump")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    var (st, body) = await c.GetRawAsync("/aweme/v1/web/user/profile/self/", null, useWebSign: true);
    Console.WriteLine($"HTTP {st}  {body.Length} 字节\n");

    // 把带 sec_uid / uid / nickname 的字段全挑出来
    try
    {
        using var doc = JsonDocument.Parse(body);
        void Walk(JsonElement el, string path, int depth)
        {
            if (depth > 3) return;
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in el.EnumerateObject())
                {
                    string np = path.Length == 0 ? p.Name : path + "." + p.Name;
                    if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Walk(p.Value, np, depth + 1);
                    else if (p.Name is "sec_uid" or "uid" or "nickname" or "unique_id" or "short_id"
                             or "user_id" or "follower_count" or "following_count" or "aweme_count")
                        Console.WriteLine($"  {np} = {p.Value}");
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var item in el.EnumerateArray()) Walk(item, $"{path}[{i++}]", depth + 1);
            }
        }
        Walk(doc.RootElement, "", 0);
    }
    catch (JsonException) { Console.WriteLine("不是 JSON：" + Short(body, 200)); }

    int idx = body.IndexOf("MS4wLjABAAAA", StringComparison.Ordinal);
    if (idx >= 0)
    {
        int end = idx;
        while (end < body.Length && "\"',& ".IndexOf(body[end]) < 0) end++;
        Console.WriteLine($"\n★ sec_uid = {body[idx..end]}");
    }
    else Console.WriteLine("\n没找到 sec_uid");
    return 0;
}

// 登录后用自己的 sec_uid 拉喜欢 / 收藏：dotnet run --project src/DouyinDesktop.Probe -- favmine
if (args.Length > 0 && args[0] == "favmine")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    // 优先用 profile/self 现拿，拿不到再读文件
    string secUid = "";
    var (_, selfBody) = await c.GetRawAsync("/aweme/v1/web/user/profile/self/", null, useWebSign: true);
    int i0 = selfBody.IndexOf("MS4wLjABAAAA", StringComparison.Ordinal);
    if (i0 >= 0)
    {
        int end = i0;
        while (end < selfBody.Length && "\"',& ".IndexOf(selfBody[end]) < 0) end++;
        secUid = selfBody[i0..end];
    }
    if (secUid.Length == 0)
    {
        string f = Path.Combine(DouyinPaths.DataDirectory, "my-sec-uid.txt");
        if (File.Exists(f)) secUid = File.ReadAllText(f).Trim();
    }

    if (secUid.Length == 0) { Console.WriteLine("拿不到自己的 sec_uid（登录失效？）"); return 1; }
    Console.WriteLine($"自己的 sec_uid = {secUid}\n");

    // ---- 喜欢 ----
    Console.WriteLine("=== 喜欢 aweme/favorite ===");
    foreach (int page in new[] { 0, 1 })
    {
        string cursor = (page * 20).ToString();
        var (st, bd) = await c.GetRawAsync("/aweme/v1/web/aweme/favorite/", new[]
        {
            new KeyValuePair<string, string>("sec_user_id", secUid),
            new KeyValuePair<string, string>("count", "20"),
            new KeyValuePair<string, string>("max_cursor", cursor),
            new KeyValuePair<string, string>("min_cursor", cursor),
        }, useWebSign: true);

        int n = 0;
        long maxCursor = -1, hasMore = -1;
        var names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(bd);
            if (doc.RootElement.TryGetProperty("aweme_list", out var al) && al.ValueKind == JsonValueKind.Array)
            {
                n = al.GetArrayLength();
                foreach (var a in al.EnumerateArray().Take(5))
                {
                    string nick = a.TryGetProperty("author", out var au) && au.TryGetProperty("nickname", out var nk)
                        ? nk.GetString() ?? "" : "";
                    string desc = a.TryGetProperty("desc", out var d) ? (d.GetString() ?? "") : "";
                    names.Add($"@{nick} {Short(desc, 24)}");
                }
            }
            if (doc.RootElement.TryGetProperty("max_cursor", out var mc) && mc.ValueKind == JsonValueKind.Number)
                maxCursor = mc.GetInt64();
            if (doc.RootElement.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.Number)
                hasMore = hm.GetInt64();
        }
        catch (JsonException) { }

        Console.WriteLine($"  第 {page + 1} 页 HTTP {st}  {bd.Length}B  作品 {n} 条  has_more={hasMore}  max_cursor={maxCursor}");
        foreach (string s in names) Console.WriteLine("      " + s);
        if (n == 0) Console.WriteLine("      原始：" + Short(bd, 140));
        await Task.Delay(1200);
    }

    // ---- 收藏：再试一批路径（登录态下）----
    Console.WriteLine("\n=== 收藏：路径探测（登录态）===");
    string[] paths =
    {
        "/aweme/v1/web/aweme/listcollection/",
        "/aweme/v1/web/aweme/collect/list/",
        "/aweme/v1/web/collect/list/",
        "/aweme/v1/web/aweme/collection/list/",
        "/aweme/v1/web/user/collect/list/",
        "/aweme/v1/web/aweme/list/collection/",
        "/aweme/v1/web/collects/list/",
        "/aweme/v1/web/aweme/collect/",
    };
    foreach (string path in paths)
    {
        try
        {
            var (st, bd) = await c.GetRawAsync(path, new[]
            {
                new KeyValuePair<string, string>("count", "10"),
                new KeyValuePair<string, string>("cursor", "0"),
                new KeyValuePair<string, string>("sec_user_id", secUid),
            }, useWebSign: true);
            Console.WriteLine($"  {path,-42} HTTP {st}  {bd.Length}B  {Short(bd, 80)}");
        }
        catch (Exception ex) { Console.WriteLine($"  {path,-42} 异常 {ex.Message}"); }
        await Task.Delay(800);
    }

    return 0;
}

// 收藏夹列表结构：dotnet run --project src/DouyinDesktop.Probe -- collections
if (args.Length > 0 && args[0] == "collections")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    var (st, bd) = await c.GetRawAsync("/aweme/v1/web/collects/list/", new[]
    {
        new KeyValuePair<string, string>("count", "20"),
        new KeyValuePair<string, string>("cursor", "0"),
    }, useWebSign: true);

    Console.WriteLine($"HTTP {st}  {bd.Length} 字节\n");

    try
    {
        using var doc = JsonDocument.Parse(bd);
        if (doc.RootElement.TryGetProperty("collects_list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine($"收藏夹 {list.GetArrayLength()} 个：");
            foreach (var g in list.EnumerateArray())
            {
                string name = g.TryGetProperty("collects_name", out var n) ? (n.GetString() ?? "") : "";
                string id = g.TryGetProperty("collects_id_str", out var i) ? (i.GetString() ?? "")
                          : g.TryGetProperty("collects_id", out var i2) ? i2.ToString() : "";
                string cnt = g.TryGetProperty("total_number", out var t) ? t.ToString()
                           : g.TryGetProperty("item_count", out var t2) ? t2.ToString() : "?";
                Console.WriteLine($"  · {name}  id={id}  数量={cnt}");
                Console.WriteLine($"    {Short(g.GetRawText(), 200)}");
            }
        }
        else
        {
            Console.WriteLine("没有 collects_list，原始：");
            Console.WriteLine(Short(bd, 1200));
        }

        foreach (string k in new[] { "has_more", "cursor", "status_code" })
            if (doc.RootElement.TryGetProperty(k, out var v)) Console.WriteLine($"  {k} = {v}");
    }
    catch (JsonException) { Console.WriteLine("非 JSON：" + Short(bd, 300)); }

    return 0;
}

// 收藏夹里的作品：dotnet run --project src/DouyinDesktop.Probe -- collectitems
if (args.Length > 0 && args[0] == "collectitems")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    const string collectsId = "7555198150416439040";

    string[] paths =
    {
        "/aweme/v1/web/collects/item/list/",
        "/aweme/v1/web/collects/video/list/",
        "/aweme/v1/web/collects/aweme/list/",
        "/aweme/v1/web/collects/detail/",
        "/aweme/v1/web/aweme/collect/list/",
        "/aweme/v1/web/collects/list/item/",
    };

    foreach (string path in paths)
    {
        foreach (string idKey in new[] { "collects_id", "collect_id" })
        {
            try
            {
                var (st, bd) = await c.GetRawAsync(path, new[]
                {
                    new KeyValuePair<string, string>(idKey, collectsId),
                    new KeyValuePair<string, string>("count", "10"),
                    new KeyValuePair<string, string>("cursor", "0"),
                }, useWebSign: true);

                int n = 0;
                try
                {
                    using var doc = JsonDocument.Parse(bd);
                    if (doc.RootElement.TryGetProperty("aweme_list", out var al) && al.ValueKind == JsonValueKind.Array)
                        n = al.GetArrayLength();
                }
                catch (JsonException) { }

                string mark = st == 200 && !bd.Contains("Unsupported path") ? " ★" : "";
                Console.WriteLine($"  {path,-38} {idKey,-11} HTTP {st}  {bd.Length}B  作品 {n}{mark}");
                if (mark.Length > 0) Console.WriteLine("      " + Short(bd, 200));
            }
            catch (Exception ex) { Console.WriteLine($"  {path,-38} {idKey,-11} 异常 {ex.Message}"); }
            await Task.Delay(700);
        }
    }

    return 0;
}

// 喜欢翻页（走 App 用的同一套 GetLikedAsync）：dotnet run --project src/DouyinDesktop.Probe -- likes
if (args.Length > 0 && args[0] == "likes")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    string secUid = await c.GetSelfSecUidAsync();
    if (secUid.Length == 0) { Console.WriteLine("拿不到 sec_uid（未登录）"); return 1; }
    Console.WriteLine($"sec_uid = {secUid}\n");

    string cursor = "0";
    int total = 0;
    for (int page = 0; page < 3; page++)
    {
        var r = await c.GetLikedAsync(secUid, cursor, 20);
        Console.WriteLine($"第 {page + 1} 页  success={r.Success}  {r.Items.Count} 条  " +
                          $"has_more={r.HasMore}  next={r.NextCursor}  {r.ElapsedMs}ms  {r.Message}");

        if (!r.Success) break;
        total += r.Items.Count;
        foreach (var it in r.Items.Take(3))
            Console.WriteLine($"      @{it.AuthorName}  {Short(it.Desc, 30)}");

        if (!r.HasMore || r.NextCursor.Length == 0 || r.NextCursor == cursor) break;
        cursor = r.NextCursor;
        await Task.Delay(1500);
    }
    Console.WriteLine($"\n合计 {total} 条");

    // 收藏也顺手验证一遍
    Console.WriteLine("\n=== 收藏 ===");
    string cid = await c.GetDefaultCollectsIdAsync();
    Console.WriteLine($"collects_id = {(cid.Length > 0 ? cid : "(无)")}");
    if (cid.Length > 0)
    {
        var cr = await c.GetCollectedAsync(cid, "0", 20);
        Console.WriteLine($"success={cr.Success}  {cr.Items.Count} 条  has_more={cr.HasMore}  " +
                          $"next={cr.NextCursor}  {cr.Message}");
        foreach (var it in cr.Items.Take(3))
            Console.WriteLine($"      @{it.AuthorName}  {Short(it.Desc, 30)}");

        // 模拟 App 里的标签过滤——崩溃就发生在这条路径（TagRule.Allows → Hashtags）
        if (cr.Success && cr.Items.Count > 0)
        {
            var rule = TagRule.Parse("美食");
            int boom = 0;
            foreach (var it in cr.Items)
            {
                try { _ = rule.Allows(it); }
                catch (Exception ex) { boom++; Console.WriteLine($"      ✗ {it.Id} 标签过滤炸了：{ex.GetType().Name}"); }
            }
            Console.WriteLine($"      标签过滤：{cr.Items.Count - boom}/{cr.Items.Count} 通过" +
                              (boom == 0 ? "（无异常）" : $"（{boom} 条异常）"));
        }
    }

    return 0;
}

// 平台标签（video_tag）的覆盖率与耗时：dotnet run --project src/DouyinDesktop.Probe -- vtagcov
// 回答"用平台标签做推荐倾向可不可行"：推荐流里没有，只能逐条调详情，先量一下代价。
if (args.Length > 0 && args[0] == "vtagcov")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    int sample = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 20;

    var sampleFeed = await c.GetFeedAsync(sample);
    if (!sampleFeed.Success) { Console.WriteLine("拉推荐失败：" + sampleFeed.Message); return 1; }

    var ids = sampleFeed.Items.Select(x => x.Id).Distinct().Take(sample).ToList();
    Console.WriteLine($"推荐流 {ids.Count} 条，开始逐条取详情…\n");

    int ok = 0, hasOfficial = 0, hasAuthor = 0;
    var officialCounter = new Dictionary<string, int>(StringComparer.Ordinal);
    var sw = System.Diagnostics.Stopwatch.StartNew();

    foreach (string id in ids)
    {
        var d = await c.GetAwemeDetailAsync(id);
        if (d is null) { Console.WriteLine($"  {id}  详情失败"); continue; }
        ok++;

        var official = d.OfficialTags;
        var author = d.Hashtags;
        if (official.Count > 0) hasOfficial++;
        if (author.Count > 0) hasAuthor++;
        foreach (string t in official) officialCounter[t] = officialCounter.GetValueOrDefault(t) + 1;

        Console.WriteLine($"  {(official.Count == 0 ? "(无平台标签)" : string.Join(" / ", official)),-30} " +
                          $"作者[{author.Count}个]");
        await Task.Delay(350);
    }

    sw.Stop();
    Console.WriteLine($"\n成功 {ok}/{ids.Count} 条，用时 {sw.Elapsed.TotalSeconds:0.0}s" +
                      $"（平均 {sw.Elapsed.TotalMilliseconds / Math.Max(1, ok):0}ms/条）");
    Console.WriteLine($"平台标签覆盖 {hasOfficial}/{ok}（{(ok == 0 ? 0 : hasOfficial * 100 / ok)}%），" +
                      $"作者标签覆盖 {hasAuthor}/{ok}（{(ok == 0 ? 0 : hasAuthor * 100 / ok)}%）");

    Console.WriteLine("\n平台标签出现次数：");
    foreach (var (t, n) in officialCounter.OrderByDescending(x => x.Value).Take(15))
        Console.WriteLine($"  {n,2}  {t}");

    return 0;
}

// 收藏作品响应的真实结构：dotnet run --project src/DouyinDesktop.Probe -- dumpcollect
if (args.Length > 0 && args[0] == "dumpcollect")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    string cid = await c.GetDefaultCollectsIdAsync();
    Console.WriteLine($"collects_id = {cid}\n");

    var (st, bd) = await c.GetRawAsync("/aweme/v1/web/collects/video/list/", new[]
    {
        new KeyValuePair<string, string>("collects_id", cid),
        new KeyValuePair<string, string>("count", "10"),
        new KeyValuePair<string, string>("cursor", "0"),
    }, useWebSign: true);

    Console.WriteLine($"HTTP {st}  {bd.Length} 字节\n");

    using var doc = JsonDocument.Parse(bd);
    Console.WriteLine("顶层字段：");
    foreach (var p in doc.RootElement.EnumerateObject())
    {
        string extra = p.Value.ValueKind == JsonValueKind.Array ? $" [{p.Value.GetArrayLength()} 项]" : "";
        Console.WriteLine($"  {p.Name,-24} {p.Value.ValueKind}{extra}");
    }

    if (doc.RootElement.TryGetProperty("aweme_list", out var al) && al.ValueKind == JsonValueKind.Array)
    {
        var head0 = al.EnumerateArray().FirstOrDefault();
        Console.WriteLine($"\naweme_list[0] 类型 = {head0.ValueKind}");
        string raw = head0.ValueKind switch
        {
            JsonValueKind.String => head0.GetString() ?? "",
            JsonValueKind.Object => head0.GetRawText(),
            _ => "",
        };
        Console.WriteLine(Short(raw, 300));

        // 逐条试反序列化，把失败的原因打出来（现在有 6/20 条解析不出来）
        Console.WriteLine($"\n逐条反序列化（共 {al.GetArrayLength()} 条）：");
        int ok = 0;
        int i = 0;
        foreach (var a in al.EnumerateArray())
        {
            string r = a.ValueKind switch
            {
                JsonValueKind.String => a.GetString() ?? "",
                JsonValueKind.Object => a.GetRawText(),
                _ => "",
            };
            try
            {
                var item = DouyinClient.ParseOne(r);
                if (item is null) Console.WriteLine($"  [{i}] null");
                else { ok++; }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{i}] 失败：{ex.GetType().Name}: {ex.Message}");
            }
            i++;
        }
        Console.WriteLine($"成功 {ok}/{al.GetArrayLength()}");
    }
    else
    {
        Console.WriteLine("\n没有 aweme_list，整段开头：");
        Console.WriteLine(Short(bd, 800));
    }

    return 0;
}

// 收藏作品有哪些可用字段（做备份功能前的调研）：dotnet run --project src/DouyinDesktop.Probe -- favfields
if (args.Length > 0 && args[0] == "favfields")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    string cid = await c.GetDefaultCollectsIdAsync();
    var (st, body) = await c.GetRawAsync("/aweme/v1/web/collects/video/list/", new[]
    {
        new KeyValuePair<string, string>("collects_id", cid),
        new KeyValuePair<string, string>("count", "1"),
        new KeyValuePair<string, string>("cursor", "0"),
    }, useWebSign: true);

    using var doc = JsonDocument.Parse(body);
    if (!doc.RootElement.TryGetProperty("aweme_list", out var al) || al.ValueKind != JsonValueKind.Array
        || al.GetArrayLength() == 0)
    {
        Console.WriteLine("没拿到作品：" + Short(body, 200));
        return 1;
    }

    var aw = al.EnumerateArray().First();
    var keys = aw.EnumerateObject().Select(p => p.Name).ToList();
    Console.WriteLine($"顶层字段 {keys.Count} 个：");
    for (int i = 0; i < keys.Count; i += 5)
        Console.WriteLine("  " + string.Join("  ", keys.Skip(i).Take(5)));

    Console.WriteLine("\n可能和「收藏/时间/媒体」有关的字段：");
    foreach (var p in aw.EnumerateObject())
    {
        string n = p.Name;
        if (!(n.Contains("collect") || n.Contains("time") || n.Contains("image")
              || n.Contains("music") || n.Contains("video") || n.Contains("statistics")
              || n.Contains("text_extra") || n.Contains("video_tag") || n.Contains("author")))
            continue;

        string val = p.Value.ValueKind switch
        {
            JsonValueKind.Null => "null",
            JsonValueKind.Array => $"[{p.Value.GetArrayLength()} 项]",
            JsonValueKind.Object => "(对象)",
            _ => Short(p.Value.ToString(), 40),
        };
        Console.WriteLine($"  {n,-22} {p.Value.ValueKind,-8} {val}");
    }

    // 互动数据 + 作者
    if (aw.TryGetProperty("statistics", out var s) && s.ValueKind == JsonValueKind.Object)
    {
        Console.WriteLine("\nstatistics：");
        foreach (var p in s.EnumerateObject()) Console.WriteLine($"  {p.Name} = {p.Value}");
    }
    if (aw.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object)
    {
        Console.WriteLine("\nauthor：");
        foreach (string k in new[] { "uid", "sec_uid", "nickname", "unique_id", "signature" })
            if (au.TryGetProperty(k, out var v)) Console.WriteLine($"  {k} = {Short(v.ToString(), 60)}");
    }
    if (aw.TryGetProperty("video", out var vd) && vd.ValueKind == JsonValueKind.Object)
    {
        Console.WriteLine("\nvideo：");
        foreach (string k in new[] { "duration", "width", "height", "ratio", "bit_rate" })
            if (vd.TryGetProperty(k, out var v))
                Console.WriteLine($"  {k} = {(v.ValueKind == JsonValueKind.Array ? $"[{v.GetArrayLength()} 项]" : Short(v.ToString(), 50))}");
    }

    return 0;
}

// 完整备份一遍收藏，验证三种格式的产物：dotnet run --project src/DouyinDesktop.Probe -- backup
if (args.Length > 0 && args[0] == "backup")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var c = DouyinClient.Create();
    c.Log += m => Console.WriteLine("    " + m);

    string cid = await c.GetDefaultCollectsIdAsync();
    if (cid.Length == 0) { Console.WriteLine("没有收藏夹（未登录？）"); return 1; }
    Console.WriteLine($"collects_id = {cid}\n");

    var all = new List<DouyinDesktop.Core.Models.Aweme>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    string cursor = "0";

    for (int page = 0; page < 60; page++)
    {
        var r = await c.GetCollectedAsync(cid, cursor, 20);
        if (!r.Success) { Console.WriteLine($"第 {page + 1} 页失败：{r.Message}"); break; }

        foreach (var a in r.Items) if (seen.Add(a.Id)) all.Add(a);
        Console.Write($"\r  第 {page + 1} 页… 累计 {all.Count} 条");

        if (!r.HasMore || r.NextCursor.Length == 0 || r.NextCursor == cursor) break;
        cursor = r.NextCursor;
        await Task.Delay(700);
    }
    Console.WriteLine();

    if (all.Count == 0) { Console.WriteLine("一条都没读到"); return 1; }

    var file = BackupExporter.Build(all, "collect");
    string dir = Path.Combine(Path.GetTempPath(), "dy-backup-" + DateTime.Now.ToString("HHmmss"));
    Directory.CreateDirectory(dir);

    var utf8 = new System.Text.UTF8Encoding(false);
    File.WriteAllText(Path.Combine(dir, "collect.json"), BackupExporter.ToJson(file), utf8);
    File.WriteAllText(Path.Combine(dir, "collect.csv"), BackupExporter.ToCsv(file), utf8);
    File.WriteAllText(Path.Combine(dir, "index.html"), BackupExporter.ToHtml(file), utf8);

    var di = new DirectoryInfo(dir);
    Console.WriteLine($"\n导出 {all.Count} 条 → {dir}");
    foreach (var f in di.GetFiles())
        Console.WriteLine($"  {f.Name,-14} {f.Length / 1024.0,8:0.0} KB");

    // 抽查：CSV 行数、HTML 卡片数、JSON 里的永久标识
    int csvLines = File.ReadAllLines(Path.Combine(dir, "collect.csv")).Length;
    string html = File.ReadAllText(Path.Combine(dir, "index.html"));
    int cards = System.Text.RegularExpressions.Regex.Matches(html, "class=\"card\"").Count;
    Console.WriteLine($"\n校验：CSV {csvLines} 行（行数会多于条数——文案里的换行是合法的多行单元格），" +
                      $"HTML {cards} 张卡片");

    var head = file.Items.FirstOrDefault();
    if (head is not null)
    {
        Console.WriteLine($"首条：{head.AwemeId}");
        Console.WriteLine($"  网页 {head.WebUrl}");
        Console.WriteLine($"  视频ID {(head.VideoId.Length > 0 ? head.VideoId : "(空)")}");
        Console.WriteLine($"  封面 {(head.CoverUrl.Length > 0 ? "有" : "无")}  平台标签 [{string.Join("/", head.OfficialTags)}]");
    }

    return 0;
}

// 弹幕接口的响应有哪些字段（想显示"弹幕总数"）：dotnet run --project src/DouyinDesktop.Probe -- dmfields
if (args.Length > 0 && args[0] == "dmfields")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var cd = DouyinClient.Create();
    cd.Log += m => Console.WriteLine("    " + m);

    var dmFeed = await cd.GetFeedAsync(12);
    if (!dmFeed.Success) { Console.WriteLine("拉推荐失败：" + dmFeed.Message); return 1; }

    foreach (var item in dmFeed.Items)
    {
        var page = await cd.GetDanmakuAsync(item.Id, 0, 32000);
        if (!page.HasItems) continue;

        Console.WriteLine($"找到有弹幕的作品 {item.Id}（本窗口 {page.Items.Count} 条）\n");

        var (st, body) = await cd.GetRawAsync("/aweme/v1/web/danmaku/list/", new[]
        {
            new KeyValuePair<string, string>("aweme_id", item.Id),
            new KeyValuePair<string, string>("item_id", item.Id),
            new KeyValuePair<string, string>("start_time", "0"),
            new KeyValuePair<string, string>("end_time", "32000"),
        }, useWebSign: true);

        Console.WriteLine($"HTTP {st}  {body.Length} 字节");
        using var doc = JsonDocument.Parse(body);
        Console.WriteLine("顶层字段：");
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            string extra = p.Value.ValueKind switch
            {
                JsonValueKind.Array => $" [{p.Value.GetArrayLength()} 项]",
                JsonValueKind.Object => " (对象)",
                JsonValueKind.String => $" = {Short(p.Value.GetString() ?? "", 30)}",
                JsonValueKind.Number => $" = {p.Value}",
                _ => "",
            };
            Console.WriteLine($"  {p.Name,-20} {p.Value.ValueKind}{extra}");
        }
        return 0;
    }

    Console.WriteLine("12 条推荐里都没弹幕，换个时间再试");
    return 1;
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
