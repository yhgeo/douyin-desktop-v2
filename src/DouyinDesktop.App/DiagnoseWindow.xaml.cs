using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace DouyinDesktop.App;

/// <summary>
/// 诊断窗口：用 WebView2 打开抖音网页版，注入脚本 hook 推荐流请求，
/// 把**真实响应**抓下来统计。
///
/// 目的：确认网页版推荐流到底有没有图集（aweme_type=68）。
/// 我们自己构造的请求扫了 58 条全是视频，需要拿浏览器的真实请求做对照。
///
/// 跑法：DouyinDesktop.exe --diagnose
/// </summary>
public partial class DiagnoseWindow : Window
{
    private int _feedCount;
    private int _totalItems;
    private int _imageItems;
    private readonly Dictionary<int, int> _typeCount = new();
    private readonly StringBuilder _report = new();
    private readonly List<string> _imageDetails = new();
    private string? _firstUrlParams;

    public DiagnoseWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            string dataDir = DouyinDesktop.Core.DouyinPaths.WebViewDiagnoseDirectory;
            Directory.CreateDirectory(dataDir);

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--no-sandbox --disable-gpu",
            };
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
            await Web.EnsureCoreWebView2Async(env);

            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // 用 CDP 监听网络：比注入 JS hook 可靠，抖音的请求不一定走 XHR
            var receiver = Web.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.responseReceived");
            receiver.DevToolsProtocolEventReceived += OnResponseReceived;
            await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.enable", "{}");

            StatusText.Text = "页面打开后自己点「推荐」、往下刷；刷够了点右下角「完成并输出报告」";
            Web.CoreWebView2.Navigate("https://www.douyin.com/");
        }
        catch (Exception ex)
        {
            StatusText.Text = "初始化失败：" + ex.Message;
            AppLog.Write("诊断窗口初始化失败：" + ex);
        }
    }

    private async void OnResponseReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;

            string requestId = root.TryGetProperty("requestId", out var rid) ? rid.GetString() ?? "" : "";
            if (requestId.Length == 0) return;

            if (!root.TryGetProperty("response", out var response)) return;
            string url = response.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

            if (!url.Contains("/web/module/feed/") && !url.Contains("/web/tab/feed/")
                && !url.Contains("/web/feed/"))
                return;

            _feedCount++;
            _firstUrlParams ??= url.Contains('?') ? url[(url.IndexOf('?') + 1)..] : url;

            // 响应体要单独取
            string bodyResult = await Web.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Network.getResponseBody", $"{{\"requestId\":\"{requestId}\"}}");

            using var bodyDoc = JsonDocument.Parse(bodyResult);
            string body = bodyDoc.RootElement.TryGetProperty("body", out var bd)
                ? bd.GetString() ?? "" : "";
            bool isBase64 = bodyDoc.RootElement.TryGetProperty("base64Encoded", out var b64)
                            && b64.ValueKind == JsonValueKind.True;
            if (isBase64 && body.Length > 0)
                body = Encoding.UTF8.GetString(Convert.FromBase64String(body));

            if (body.Length == 0) return;

            using var payload = JsonDocument.Parse(body);
            var items = ExtractAwemes(payload.RootElement);

            foreach (var item in items)
            {
                _totalItems++;
                int type = item.TryGetProperty("aweme_type", out var t) && t.ValueKind == JsonValueKind.Number
                    ? t.GetInt32() : -1;
                _typeCount[type] = _typeCount.GetValueOrDefault(type) + 1;

                bool hasImages = item.TryGetProperty("images", out var imgs)
                                 && imgs.ValueKind == JsonValueKind.Array
                                 && imgs.GetArrayLength() > 0;
                if (hasImages || type == 68)
                {
                    _imageItems++;
                    // 抓到图集就把细节记下来，方便确认是不是真的图文作品
                    string id = item.TryGetProperty("aweme_id", out var aid) ? aid.GetString() ?? "" : "";
                    string desc = item.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "";
                    int imgCount = hasImages ? imgs.GetArrayLength() : 0;
                    _imageDetails.Add($"  {id}  type={type}  images={imgCount}  {Truncate(desc, 30)}");
                }
            }

            StatusText.Text = $"已抓到 {_feedCount} 个推荐流响应，共 {_totalItems} 条，其中图集 {_imageItems} 条";
        }
        catch (Exception ex)
        {
            // getResponseBody 偶尔会失败（响应体已被丢弃），忽略即可
            AppLog.Write("诊断解析失败：" + ex.Message);
        }
    }

    /// <summary>推荐流是 cards[].aweme（字符串），tab 流是 aweme_list（对象）。</summary>
    private static List<JsonElement> ExtractAwemes(JsonElement root)
    {
        var list = new List<JsonElement>();

        if (root.TryGetProperty("cards", out var cards) && cards.ValueKind == JsonValueKind.Array)
        {
            foreach (var card in cards.EnumerateArray())
            {
                if (!card.TryGetProperty("aweme", out var aw)) continue;
                if (aw.ValueKind == JsonValueKind.Object) list.Add(aw);
                else if (aw.ValueKind == JsonValueKind.String)
                {
                    try { using var inner = JsonDocument.Parse(aw.GetString() ?? ""); list.Add(inner.RootElement.Clone()); }
                    catch (JsonException) { }
                }
            }
        }
        else if (root.TryGetProperty("aweme_list", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
                if (a.ValueKind == JsonValueKind.Object) list.Add(a);
        }

        return list;
    }

    private void OnFinishClick(object sender, RoutedEventArgs e)
    {
        _report.Clear();
        _report.AppendLine("===== 推荐流诊断结果 =====");
        _report.AppendLine($"响应数：{_feedCount}");
        _report.AppendLine($"条目总数：{_totalItems}");
        _report.AppendLine($"图集数（aweme_type=68 或 images 非空）：{_imageItems}");
        _report.AppendLine("aweme_type 分布：" + string.Join("，",
            _typeCount.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}")));

        if (_imageDetails.Count > 0)
        {
            _report.AppendLine();
            _report.AppendLine("图集明细：");
            foreach (string line in _imageDetails) _report.AppendLine(line);
        }

        if (_firstUrlParams is not null)
        {
            _report.AppendLine();
            _report.AppendLine("浏览器真实请求参数：");
            foreach (string pair in _firstUrlParams.Split('&'))
            {
                int eq = pair.IndexOf('=');
                string key = eq > 0 ? pair[..eq] : pair;
                string val = eq > 0 ? pair[(eq + 1)..] : "";

                // 这几个是关键标识，要完整值才能和我们的对比
                bool keepFull = key is "uifid" or "verifyFp" or "fp" or "webid" or "pre_log_id";
                _report.AppendLine($"  {key} = {(keepFull ? val : (val.Length > 40 ? $"<{val.Length} 字符>" : val))}");
            }
        }

        // Cookie 清单（只要名字，值不写），用来对比我们抓的登录态缺哪些
        try
        {
            var cookies = Web.CoreWebView2.CookieManager
                .GetCookiesAsync("https://www.douyin.com").GetAwaiter().GetResult();

            _report.AppendLine();
            _report.AppendLine($"浏览器 Cookie 数量：{cookies.Count}");
            _report.AppendLine("Cookie 名：" + string.Join(", ",
                cookies.Select(c => c.Name).OrderBy(n => n)));

            var uifidCookie = cookies.FirstOrDefault(c => c.Name == "UIFID");
            if (uifidCookie is not null)
                _report.AppendLine($"Cookie 里的 UIFID = {uifidCookie.Value}");
        }
        catch (Exception ex)
        {
            _report.AppendLine("读取 Cookie 失败：" + ex.Message);
        }

        AppLog.Write(_report.ToString());
        StatusText.Text = $"完成：{_feedCount} 个响应 / {_totalItems} 条，图集 {_imageItems} 条（报告已写入日志）";
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
