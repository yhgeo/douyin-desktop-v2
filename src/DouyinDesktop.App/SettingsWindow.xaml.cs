using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

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
        RefreshAccount();
    }

    // ================= 左侧导航 =================

    private void OnNavAccount(object sender, RoutedEventArgs e) => SwitchPage("account");
    private void OnNavKeys(object sender, RoutedEventArgs e) => SwitchPage("keys");
    private void OnNavPlay(object sender, RoutedEventArgs e) => SwitchPage("play");
    private void OnNavAbout(object sender, RoutedEventArgs e) => SwitchPage("about");

    private void OnImageOnlyChanged(object sender, RoutedEventArgs e)
    {
        _settings.ImageOnly = ImageOnlyCheck.IsChecked == true;
        _settings.Save();
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

    private void RefreshAccount()
    {
        bool logged = _client.Session?.IsLoggedIn == true;

        AccountStatus.Text = logged ? "已登录" : "未登录（游客态）";
        AccountStatus.Foreground = logged
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xC8, 0xC8, 0xD2))
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x8C, 0x8C, 0x99));

        LoginBtn.Content = logged ? "重新登录" : "扫码登录";
        LogoutBtn.Visibility = logged ? Visibility.Visible : Visibility.Collapsed;
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
