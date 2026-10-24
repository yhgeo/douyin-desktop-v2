using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DouyinDesktop.Core;

namespace DouyinDesktop.App;

/// <summary>
/// 快捷键设置。点"修改"后进入捕获状态，按任意键绑定；
/// 被别的动作占用的键会自动从那边解除。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly KeyMap _map;
    private readonly ObservableCollection<KeyRow> _rows = new();
    private readonly DouyinDesktop.Core.Api.DouyinClient _client;
    private readonly Func<Task<bool>> _loginAction;
    private readonly Action _logoutAction;
    private readonly AppSettings _settings;
    private KeyRow? _capturing;

    public SettingsWindow(KeyMap map,
                          AppSettings settings,
                          DouyinDesktop.Core.Api.DouyinClient client,
                          Func<Task<bool>> loginAction,
                          Action logoutAction)
    {
        InitializeComponent();

        _map = map;
        _settings = settings;
        _client = client;
        _loginAction = loginAction;
        _logoutAction = logoutAction;

        foreach (var def in KeyMap.Actions) _rows.Add(new KeyRow(_map, def));
        KeyList.ItemsSource = _rows;

        DataPathText.Text = DouyinDesktop.Core.DouyinPaths.DataDirectory;
        ImageOnlyCheck.IsChecked = settings.ImageOnly;

        // 标签过滤：先填文本（会触发一次 TextChanged，那里有 null guard）
        if (TagRulesBox is not null) TagRulesBox.Text = settings.TagRules;
        UpdateTagRulesHint();
        LoadDanmakuUi();
        RefreshAccount();

        // 后台检查完登录状态后，设置页开着也能实时更新
        LoginHealth.Changed += OnHealthChanged;
        Closed += (_, _) => LoginHealth.Changed -= OnHealthChanged;
    }

    private void OnHealthChanged() => Dispatcher.BeginInvoke(UpdateHealthText);

    // ================= 左侧导航 =================

    private void OnNavAccount(object sender, RoutedEventArgs e) => SwitchPage("account");
    private void OnNavKeys(object sender, RoutedEventArgs e) => SwitchPage("keys");
    private void OnNavPlay(object sender, RoutedEventArgs e) => SwitchPage("play");
    private void OnNavAbout(object sender, RoutedEventArgs e) => SwitchPage("about");

    /// <summary>
    /// 标签过滤规则改了。
    ///
    /// ⚠️ WPF 在 XAML 解析阶段就会触发一次 TextChanged，那时构造函数里的字段
    /// （_settings、其它命名控件）还是 null —— 必须 guard，否则设置窗口直接弹"出错了"。
    /// </summary>
    private void OnTagRulesChanged(object sender, TextChangedEventArgs e)
    {
        if (_settings is null || TagRulesBox is null) return;

        _settings.TagRules = TagRulesBox.Text;
        _settings.Save();
        UpdateTagRulesHint();
    }

    private void UpdateTagRulesHint()
    {
        if (TagRulesHint is null) return;

        var rule = TagRule.Parse(TagRulesBox?.Text);
        TagRulesHint.Text = rule.IsEmpty
            ? "留空 = 不过滤，按抖音原样推荐"
            : $"当前：{rule.Describe()}";
    }

    private void OnImageOnlyChanged(object sender, RoutedEventArgs e)
    {
        _settings.ImageOnly = ImageOnlyCheck.IsChecked == true;
        _settings.Save();
    }

    private void OnDanmakuChanged(object sender, RoutedEventArgs e)
    {
        _settings.Danmaku = DanmakuCheck.IsChecked == true;
        _settings.Save();
        if (!_danmakuLoading) DanmakuOptions.IsEnabled = _settings.Danmaku;
    }

    private void OnOverlayRendererChanged(object sender, RoutedEventArgs e)
    {
        if (_danmakuLoading) return;

        _settings.DanmakuRenderer = OverlayCheck.IsChecked == true ? "overlay" : "ass";
        _settings.Save();
    }

    // ================= 弹幕外观 =================

    private bool _danmakuLoading;

    /// <summary>把配置里的值灌进控件。灌的过程中别回写，否则初始化就会触发一堆保存。</summary>
    private void LoadDanmakuUi()
    {
        _danmakuLoading = true;
        try
        {
            DanmakuCheck.IsChecked = _settings.Danmaku;
            OverlayCheck.IsChecked = _settings.DanmakuRenderer == "overlay";
            FontSlider.Value = Math.Clamp(_settings.DanmakuFontPercent, FontSlider.Minimum, FontSlider.Maximum);
            OpacitySlider.Value = Math.Clamp(_settings.DanmakuOpacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
            OutlineSlider.Value = Math.Clamp(_settings.DanmakuOutlinePercent, OutlineSlider.Minimum, OutlineSlider.Maximum);
            SpeedSlider.Value = Math.Clamp(_settings.DanmakuSpeed, SpeedSlider.Minimum, SpeedSlider.Maximum);

            UpdateDanmakuLabels();
            UpdateAreaButtons();
            DanmakuOptions.IsEnabled = _settings.Danmaku;
        }
        finally
        {
            _danmakuLoading = false;
        }
    }

    private void UpdateDanmakuLabels()
    {
        FontValue.Text = $"{FontSlider.Value:0.0}%";
        OpacityValue.Text = $"{OpacitySlider.Value:0}%";
        OutlineValue.Text = $"{OutlineSlider.Value:0}%";
        SpeedValue.Text = $"{SpeedSlider.Value:0.#} 秒";
    }

    private void OnDanmakuStyleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 解析阶段滑杆的 Value 也会变一次，那时 _settings 和别的控件都还没建好
        if (_danmakuLoading || _settings is null || FontValue is null) return;

        _settings.DanmakuFontPercent = Math.Round(FontSlider.Value, 1);
        _settings.DanmakuOpacity = (int)Math.Round(OpacitySlider.Value);
        _settings.DanmakuOutlinePercent = Math.Round(OutlineSlider.Value, 1);
        _settings.DanmakuSpeed = Math.Round(SpeedSlider.Value, 1);

        UpdateDanmakuLabels();
        _settings.Save();
    }

    private void OnDanmakuAreaClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not string area) return;

        _settings.DanmakuArea = area;
        _settings.Save();
        UpdateAreaButtons();
    }

    private void UpdateAreaButtons()
    {
        foreach (var (btn, id) in new (Button, string)[]
                 {
                     (AreaTop, "top"), (AreaMiddle, "middle"),
                     (AreaBottom, "bottom"), (AreaFull, "full"),
                 })
        {
            btn.Tag = _settings.DanmakuArea == id ? "active" : null;
        }
    }

    private void SwitchPage(string page)
    {
        NavAccount.Tag = page == "account" ? "active" : null;
        NavKeys.Tag = page == "keys" ? "active" : null;
        NavPlay.Tag = page == "play" ? "active" : null;
        NavAbout.Tag = page == "about" ? "active" : null;

        PageAccount.Visibility = page == "account" ? Visibility.Visible : Visibility.Collapsed;
        PageKeys.Visibility = page == "keys" ? Visibility.Visible : Visibility.Collapsed;
        PagePlay.Visibility = page == "play" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe",
                DouyinDesktop.Core.DouyinPaths.DataDirectory);
        }
        catch (Exception ex)
        {
            HintText.Text = "打不开：" + ex.Message;
        }
    }

    /// <summary>把 LoginHealth 的状态画到账号区块（过期用红色，正常用灰）。</summary>
    private void UpdateHealthText()
    {
        if (HealthText is null) return;

        // 内存里的会话可能还没加载（启动后直接"接着上次看"不会拉数据），
        // 拿磁盘上的那份兜底，避免把"还没加载"显示成"未登录"。
        var session = _client.Session ?? DouyinDesktop.Core.Api.DouyinSession.Load();
        bool logged = session?.IsLoggedIn == true;

        // 有 Cookie 却显示"未登录"：健康检查在会话就绪前误判过。
        // 以事实为准，别让同一屏同时出现"已登录"和"未登录"。
        string health = logged && LoginHealth.Current == LoginHealth.State.Guest
            ? "待检查"
            : LoginHealth.Describe();

        string expiry = SessionExpiry.Describe(session?.LoginCookies);
        HealthText.Text = $"登录状态：{health}　·　{expiry}";
        HealthText.Foreground = LoginHealth.Current == LoginHealth.State.Expired
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFE, 0x2C, 0x55))
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x5A, 0x5A, 0x66));
    }

    private void RefreshAccount()
    {
        var session = _client.Session ?? DouyinDesktop.Core.Api.DouyinSession.Load();
        bool logged = session?.IsLoggedIn == true;

        AccountStatus.Text = logged ? "已登录" : "未登录（游客态）";
        AccountStatus.Foreground = logged
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xC8, 0xC8, 0xD2))
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x8C, 0x8C, 0x99));

        LoginBtn.Content = logged ? "重新登录" : "扫码登录";
        LogoutBtn.Visibility = logged ? Visibility.Visible : Visibility.Collapsed;

        // 登录健康度：靠"能不能读到评论"判断，主窗口每次加载视频后自动更新
        UpdateHealthText();

        if (logged) _ = VerifyLoginAsync();
    }

    /// <summary>
    /// 本地有 Cookie 不等于服务端认这个登录态（过期了照样是"已登录"）。
    /// 这里实际问一次服务端，失效就把状态标出来，别让用户以为还能点赞/关注。
    /// </summary>
    private async Task VerifyLoginAsync()
    {
        try
        {
            bool? ok = await _client.VerifyLoginAsync();
            if (ok == true) return;

            if (ok == false)
            {
                AccountStatus.Text = "登录已失效，请重新扫码";
                AccountStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFE, 0x2C, 0x55));
                HintText.Text = "点赞、关注、评论都需要有效登录，点「重新登录」扫码即可。";
                LoginBtn.Content = "重新登录";
                LogoutBtn.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            // 网络问题判断不了就保持原样
        }
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        bool ok = await _loginAction();
        if (!ok) return;

        RefreshAccount();
        HintText.Text = "登录成功";
        Close();
    }

    private void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        _logoutAction();
        RefreshAccount();
        HintText.Text = "已退出登录";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 和主窗口一样断开输入法，否则按字母键会被 IME 吃掉
        ImmAssociateContext(new System.Windows.Interop.WindowInteropHelper(this).Handle, IntPtr.Zero);
    }

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hImc);

    private void OnRebindClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: KeyRow row }) return;

        if (_capturing is not null) _capturing.IsCapturing = false;

        _capturing = row;
        row.IsCapturing = true;
        HintText.Text = $"按下要绑定到「{row.Label}」的键…";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturing is null) return;

        e.Handled = true;

        // Esc 取消绑定，不当作按键
        if (e.Key == Key.Escape)
        {
            _capturing.IsCapturing = false;
            _capturing = null;
            HintText.Text = "";
            return;
        }

        // 单独的修饰键不算
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        var row = _capturing;
        _capturing = null;
        row.IsCapturing = false;

        _map.Set(row.ActionId, e.Key);

        foreach (var r in _rows) r.Refresh();
        HintText.Text = $"「{row.Label}」已改为 {KeyMap.KeyText(e.Key)}";
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: KeyRow row }) return;

        // Key.None 表示不绑定：ActionFor 永远匹配不到，等于这个功能没有快捷键
        _map.Set(row.ActionId, Key.None);
        foreach (var r in _rows) r.Refresh();
        HintText.Text = $"「{row.Label}」已取消绑定（方向键 / WASD 别名仍可用）";
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _map.ResetAll();
        foreach (var r in _rows) r.Refresh();
        HintText.Text = "已恢复默认";
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

/// <summary>设置窗口里的一行。</summary>
public sealed class KeyRow : INotifyPropertyChanged
{
    private readonly KeyMap _map;
    private readonly KeyMap.ActionDef _def;
    private bool _capturing;

    public KeyRow(KeyMap map, KeyMap.ActionDef def)
    {
        _map = map;
        _def = def;
    }

    public string ActionId => _def.Id;
    public string Label => _def.Label;

    public string AliasText => _def.Aliases.Length > 0
        ? "另可用 " + string.Join(" / ", _def.Aliases.Select(KeyMap.KeyText))
        : "";

    public string KeyText => _capturing
        ? "请按键…"
        : KeyMap.KeyText(_map.Get(_def.Id));

    public bool IsCapturing
    {
        get => _capturing;
        set
        {
            _capturing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyText)));
        }
    }

    public void Refresh()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyText)));

    public event PropertyChangedEventHandler? PropertyChanged;
}
