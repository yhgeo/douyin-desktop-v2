using System.IO;
using System.Text.Json;
using DouyinDesktop.Core;
using Microsoft.Web.WebView2.Core;

namespace DouyinDesktop.App;

/// <summary>
/// 把 <c>data/session.json</c> 里保存的登录 Cookie **反向注入**回 WebView2。
///
/// 为什么需要：抖音的 <c>sessionid</c> 是**会话 Cookie**（没有过期时间），
/// WebView2 进程一退出就丢了。所以内置浏览器每次重新打开都是「未登录」——
/// 页面会显示"登录后即可观看喜欢、收藏的视频"，页面也就不会发
/// <c>aweme/favorite</c>、<c>search/item</c> 这些要登录态的请求。
///
/// 这解释了之前两个"怎么都抓不到"的现象：
///   · 抓搜索请求：抓不到，因为页面是游客态，搜索接口直接 2483
///   · 抓喜欢列表：抓不到，因为游客态根本不渲染喜欢 tab
///
/// **只注入登录凭据，不碰设备标识**（webid / ttwid / s_v_web_id 保持浏览器里原样），
/// 所以不会造成设备漂移。反过来说，注入完如果 webid 变了，那是别的原因，要查。
/// </summary>
public static class WebCookieInject
{
    /// <summary>从 session.json 读登录 Cookie 头。没有就返回空串。</summary>
    public static string LoadLoginCookies()
    {
        try
        {
            string path = DouyinPaths.SessionFile;
            if (!File.Exists(path)) return "";

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("LoginCookies", out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        }
        catch (Exception ex)
        {
            AppLog.Write("[注入] 读 session.json 失败：" + ex.Message);
        }
        return "";
    }

    /// <summary>把 Cookie 头逐条写进 WebView2，返回写入条数。</summary>
    public static int Inject(CoreWebView2 core, string cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader)) return 0;

        var cm = core.CookieManager;
        int n = 0;

        foreach (string raw in cookieHeader.Split(';'))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;

            int eq = part.IndexOf('=');
            if (eq <= 0) continue;

            string name = part[..eq].Trim();
            string value = part[(eq + 1)..].Trim();
            if (name.Length == 0 || value.Length == 0) continue;

            try
            {
                var ck = cm.CreateCookie(name, value, ".douyin.com", "/");
                ck.IsHttpOnly = false;
                ck.IsSecure = true;
                ck.SameSite = CoreWebView2CookieSameSiteKind.Lax;
                cm.AddOrUpdateCookie(ck);
                n++;
            }
            catch (Exception ex)
            {
                AppLog.Write($"[注入] {name} 失败：{ex.Message}");
            }
        }

        return n;
    }
}
