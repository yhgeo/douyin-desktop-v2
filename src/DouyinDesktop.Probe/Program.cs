using DouyinDesktop.Core.Api;

// 数据层验证探针：只依赖 Core，不写任何硬编码凭据。
// 跑法（项目根目录）：dotnet run --project src/DouyinDesktop.Probe
//
// 判定标准：能自己拿到凭据 → 能取到推荐流 → 能换出可播放的 MP4 字节。

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
